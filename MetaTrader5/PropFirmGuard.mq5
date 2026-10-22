//+------------------------------------------------------------------+
//|                                                PropFirmGuard.mq5 |
//|              Daily loss and max drawdown protection for MetaTrader 5 |
//+------------------------------------------------------------------+
#property copyright   "PropBot Studio"
#property link        "https://propbotstudio.fr/en/"
#property version     "1.02"
#property description "Prop Firm Guard protects challenge and funded accounts."
#property description "It watches the daily loss and the maximum drawdown every second."
#property description "When a limit is reached, it closes every position, deletes pending orders"
#property description "and closes any new trade until the limit resets."

#include <Trade\Trade.mqh>

//--- how the daily loss reference is taken at the start of each day
enum ENUM_DAILY_REF
  {
   DAILY_REF_BALANCE = 0,   // Balance at day start
   DAILY_REF_HIGHER  = 1    // Higher of balance and equity at day start
  };

//--- how the maximum drawdown is measured
enum ENUM_DD_MODE
  {
   DD_STATIC   = 0,         // Static (from account size)
   DD_TRAILING = 1          // Trailing (from highest equity)
  };

input group "Account"
input double         InpAccountSize   = 0;                // Account size (0 = current balance)

input group "Limits (% of account size, 0 = off)"
input double         InpMaxDailyLoss  = 5.0;              // Max daily loss (%)
input double         InpMaxDrawdown   = 10.0;             // Max total drawdown (%)
input double         InpSafetyMargin  = 0.0;              // Safety margin: act earlier by (%)
input ENUM_DAILY_REF InpDailyRef      = DAILY_REF_HIGHER; // Daily loss reference
input ENUM_DD_MODE   InpDrawdownMode  = DD_STATIC;        // Drawdown mode

input group "Daily reset"
input int            InpResetHour     = 0;                // Reset hour (server time, 0-23)

input group "Behaviour"
input bool           InpBlockAfterHit = true;             // Keep closing new trades while a limit is hit
input double         InpWarnAt        = 80.0;             // Warn when this % of a limit is used (0 = off)
input bool           InpPushAlerts    = false;            // Send push notifications
input bool           InpResetState    = false;            // Reset saved state on start

//--- state
CTrade   g_trade;
string   g_prefix      = "";
double   g_size        = 0.0;   // account size used for the limits
double   g_dayRef      = 0.0;   // daily loss reference for the current day
datetime g_dayStart    = 0;     // start of the current trading day
double   g_peak        = 0.0;   // highest equity (trailing drawdown)
bool     g_dailyHit    = false;
bool     g_totalHit    = false;
bool     g_warnedDaily = false;
bool     g_warnedTotal = false;
bool     g_mustFlatten = false; // close everything once after a breach
datetime g_lastFailLog = 0;
bool     g_ready       = false; // state initialised with a connected, funded account

//+------------------------------------------------------------------+
//| Small helpers                                                    |
//+------------------------------------------------------------------+
bool DailyOn() { return InpMaxDailyLoss > 0.0; }
bool TotalOn() { return InpMaxDrawdown  > 0.0; }

double DailyLimit() { return g_size * (InpMaxDailyLoss - InpSafetyMargin) / 100.0; }
double TotalLimit() { return g_size * (InpMaxDrawdown  - InpSafetyMargin) / 100.0; }
double TotalRef()   { return (InpDrawdownMode == DD_TRAILING ? g_peak : g_size); }
double DailyFloor() { return g_dayRef - DailyLimit(); }
double TotalFloor() { return TotalRef() - TotalLimit(); }

double UsedPct(const double loss, const double limit)
  {
   if(limit <= 0.0)
      return 0.0;
   return MathMax(0.0, loss / limit * 100.0);
  }

bool TradingAllowed()
  {
   return TerminalInfoInteger(TERMINAL_TRADE_ALLOWED) && MQLInfoInteger(MQL_TRADE_ALLOWED);
  }

datetime ServerNow()
  {
   if(MQLInfoInteger(MQL_TESTER))
      return TimeCurrent();
   datetime t = TimeTradeServer();
   return (t > 0 ? t : TimeCurrent());
  }

//--- start of the trading day containing "now", with the reset hour applied
datetime TradingDayStart(const datetime now)
  {
   int shift = InpResetHour * 3600;
   MqlDateTime dt;
   TimeToStruct(now - shift, dt);
   dt.hour = 0;
   dt.min  = 0;
   dt.sec  = 0;
   return StructToTime(dt) + shift;
  }

//+------------------------------------------------------------------+
//| State saved in terminal global variables (survives restarts)     |
//+------------------------------------------------------------------+
string Key(const string name) { return g_prefix + name; }

bool LoadValue(const string name, double &value)
  {
   if(!GlobalVariableCheck(Key(name)))
      return false;
   value = GlobalVariableGet(Key(name));
   return true;
  }

void SaveState()
  {
   if(g_prefix == "")
      return;
   GlobalVariableSet(Key("size"),     g_size);
   GlobalVariableSet(Key("dayRef"),   g_dayRef);
   GlobalVariableSet(Key("dayStart"), (double)g_dayStart);
   GlobalVariableSet(Key("peak"),     g_peak);
   GlobalVariableSet(Key("dailyHit"), g_dailyHit ? 1.0 : 0.0);
   GlobalVariableSet(Key("totalHit"), g_totalHit ? 1.0 : 0.0);
   GlobalVariablesFlush();
  }

void StartNewDay(const datetime dayStart)
  {
   double balance = AccountInfoDouble(ACCOUNT_BALANCE);
   double equity  = AccountInfoDouble(ACCOUNT_EQUITY);
   g_dayStart    = dayStart;
   g_dayRef      = (InpDailyRef == DAILY_REF_HIGHER ? MathMax(balance, equity) : balance);
   g_dailyHit    = false;
   g_warnedDaily = false;
   SaveState();
  }

//+------------------------------------------------------------------+
//| Messages                                                         |
//+------------------------------------------------------------------+
void Notify(const string text)
  {
   string msg = "Prop Firm Guard: " + text;
   Print(msg);
   if(MQLInfoInteger(MQL_TESTER))
      return;
   Alert(msg);
   if(InpPushAlerts)
      SendNotification(msg);
  }

void LogFailure(const string what, const ulong ticket)
  {
   if(TimeLocal() - g_lastFailLog < 30)
      return;
   g_lastFailLog = TimeLocal();
   PrintFormat("Prop Firm Guard: could not %s #%I64u (retcode %u, %s). Retrying.",
               what, ticket, g_trade.ResultRetcode(), g_trade.ResultRetcodeDescription());
  }

//+------------------------------------------------------------------+
//| Close all positions and delete all pending orders                |
//| Returns true when nothing is left open                           |
//+------------------------------------------------------------------+
bool CloseEverything()
  {
   bool done = true;

   for(int i = PositionsTotal() - 1; i >= 0; i--)
     {
      ulong ticket = PositionGetTicket(i);
      if(ticket == 0)
        {
         done = false;
         continue;
        }
      string symbol = PositionGetString(POSITION_SYMBOL);
      double volume = PositionGetDouble(POSITION_VOLUME);
      g_trade.SetTypeFillingBySymbol(symbol);
      if(!g_trade.PositionClose(ticket))
        {
         done = false;
         LogFailure("close position", ticket);
        }
      else
         PrintFormat("Prop Firm Guard: closed position #%I64u (%s, %.2f lots).", ticket, symbol, volume);
     }

   for(int i = OrdersTotal() - 1; i >= 0; i--)
     {
      ulong ticket = OrderGetTicket(i);
      if(ticket == 0)
        {
         done = false;
         continue;
        }
      string osymbol = OrderGetString(ORDER_SYMBOL);
      if(!g_trade.OrderDelete(ticket))
        {
         done = false;
         LogFailure("delete order", ticket);
        }
      else
         PrintFormat("Prop Firm Guard: deleted pending order #%I64u (%s).", ticket, osymbol);
     }

   return done && PositionsTotal() == 0 && OrdersTotal() == 0;
  }

//+------------------------------------------------------------------+
//| On-chart status                                                  |
//+------------------------------------------------------------------+
void UpdatePanel(const double equity)
  {
   if(MQLInfoInteger(MQL_TESTER) && !MQLInfoInteger(MQL_VISUAL_MODE))
      return;

   string status = "Active";
   if(g_totalHit)
      status = "BLOCKED - max drawdown reached";
   else
      if(g_dailyHit)
         status = "PAUSED - daily loss limit reached";

   string text = "Prop Firm Guard\n";
   text += "Status: " + status + "\n";
   if(!TradingAllowed())
      text += "WARNING: Algo Trading is disabled, the guard cannot close trades!\n";
   text += StringFormat("Equity: %.2f   Account size: %.2f\n", equity, g_size);
   if(DailyOn())
      text += StringFormat("Daily loss: %.2f / %.2f (%.0f%% used)   Floor: %.2f\n",
                           MathMax(0.0, g_dayRef - equity), DailyLimit(),
                           UsedPct(g_dayRef - equity, DailyLimit()), DailyFloor());
   if(TotalOn())
      text += StringFormat("Drawdown: %.2f / %.2f (%.0f%% used)   Floor: %.2f\n",
                           MathMax(0.0, TotalRef() - equity), TotalLimit(),
                           UsedPct(TotalRef() - equity, TotalLimit()), TotalFloor());
   text += "Next daily reset: " + TimeToString(g_dayStart + 86400, TIME_DATE | TIME_MINUTES) + " (server time)";
   Comment(text);
  }

//+------------------------------------------------------------------+
//| Load or create the state once the account is connected           |
//| Returns false while the account data is not available yet        |
//+------------------------------------------------------------------+
bool TryInitState()
  {
   double balance = AccountInfoDouble(ACCOUNT_BALANCE);
   double equity  = AccountInfoDouble(ACCOUNT_EQUITY);
   if(!MQLInfoInteger(MQL_TESTER) && !TerminalInfoInteger(TERMINAL_CONNECTED))
      return false;
   if(AccountInfoInteger(ACCOUNT_LOGIN) == 0 || balance <= 0.0 || equity <= 0.0)
      return false;

   string server = AccountInfoString(ACCOUNT_SERVER);
   if(StringLen(server) > 30)
      server = StringSubstr(server, 0, 30);
   g_prefix = StringFormat("PFG_%I64d_%s_", AccountInfoInteger(ACCOUNT_LOGIN), server);

   double v = 0.0;
//--- discard a saved state that was created without valid account data
   if(InpResetState || (LoadValue("size", v) && v <= 0.0))
      GlobalVariablesDeleteAll(g_prefix);

//--- account size
   if(InpAccountSize > 0.0)
      g_size = InpAccountSize;
   else
      if(LoadValue("size", v) && v > 0.0)
         g_size = v;
      else
         g_size = balance;

//--- highest equity for the trailing drawdown
   if(LoadValue("peak", v) && v > 0.0)
      g_peak = v;
   else
      g_peak = MathMax(g_size, equity);

//--- locks from a previous session
   g_dailyHit = (LoadValue("dailyHit", v) && v > 0.5);
   g_totalHit = (LoadValue("totalHit", v) && v > 0.5);

//--- same trading day as last session: keep its reference, otherwise start a new day
   datetime today = TradingDayStart(ServerNow());
   double savedStart = 0.0, savedRef = 0.0;
   if(LoadValue("dayStart", savedStart) && (datetime)savedStart == today &&
      LoadValue("dayRef", savedRef) && savedRef > 0.0)
     {
      g_dayStart = today;
      g_dayRef   = savedRef;
     }
   else
      StartNewDay(today);

   SaveState();
   g_ready = true;
   PrintFormat("Prop Firm Guard: started on account %I64d (%s), account size %.2f",
               AccountInfoInteger(ACCOUNT_LOGIN), AccountInfoString(ACCOUNT_SERVER), g_size);
   return true;
  }

//+------------------------------------------------------------------+
//| Main check, called on every tick and every second                |
//+------------------------------------------------------------------+
void Check()
  {
//--- wait for a connected account before doing anything
   if(!g_ready && !TryInitState())
     {
      if(!MQLInfoInteger(MQL_TESTER))
         Comment("Prop Firm Guard\nWaiting for the account connection...");
      return;
     }
   if(!MQLInfoInteger(MQL_TESTER) && !TerminalInfoInteger(TERMINAL_CONNECTED))
     {
      Comment("Prop Firm Guard\nConnection lost - monitoring paused until the account reconnects.");
      return;
     }
//--- new trading day: new reference, daily lock released
   datetime today = TradingDayStart(ServerNow());
   if(today != g_dayStart)
     {
      StartNewDay(today);
      PrintFormat("Prop Firm Guard: new trading day, daily loss reference %.2f", g_dayRef);
     }

   double equity = AccountInfoDouble(ACCOUNT_EQUITY);
   if(equity <= 0.0)
      return;

//--- trailing drawdown follows the highest equity
   if(InpDrawdownMode == DD_TRAILING && equity > g_peak)
     {
      g_peak = equity;
      GlobalVariableSet(Key("peak"), g_peak);
     }

//--- limits
   if(DailyOn() && !g_dailyHit && equity <= DailyFloor())
     {
      g_dailyHit    = true;
      g_mustFlatten = true;
      SaveState();
      Notify(StringFormat("daily loss limit reached (equity %.2f, floor %.2f). All trades are closed until the daily reset.",
                          equity, DailyFloor()));
     }
   if(TotalOn() && !g_totalHit && equity <= TotalFloor())
     {
      g_totalHit    = true;
      g_mustFlatten = true;
      SaveState();
      Notify(StringFormat("maximum drawdown reached (equity %.2f, floor %.2f). Trading is blocked. Restart with 'Reset saved state' to unlock.",
                          equity, TotalFloor()));
     }

//--- early warnings
   if(InpWarnAt > 0.0)
     {
      if(DailyOn() && !g_dailyHit)
        {
         double used = UsedPct(g_dayRef - equity, DailyLimit());
         if(!g_warnedDaily && used >= InpWarnAt)
           {
            g_warnedDaily = true;
            Notify(StringFormat("%.0f%% of the daily loss limit used.", used));
           }
         else
            if(g_warnedDaily && used < InpWarnAt / 2.0)
               g_warnedDaily = false;
        }
      if(TotalOn() && !g_totalHit)
        {
         double used = UsedPct(TotalRef() - equity, TotalLimit());
         if(!g_warnedTotal && used >= InpWarnAt)
           {
            g_warnedTotal = true;
            Notify(StringFormat("%.0f%% of the maximum drawdown used.", used));
           }
         else
            if(g_warnedTotal && used < InpWarnAt / 2.0)
               g_warnedTotal = false;
        }
     }

//--- enforcement
   bool locked = (g_dailyHit || g_totalHit);
   if(g_mustFlatten || (locked && InpBlockAfterHit))
     {
      if(PositionsTotal() > 0 || OrdersTotal() > 0)
        {
         if(CloseEverything())
            g_mustFlatten = false;
        }
      else
         g_mustFlatten = false;
     }

   UpdatePanel(equity);
  }

//+------------------------------------------------------------------+
//| Expert initialization                                            |
//+------------------------------------------------------------------+
int OnInit()
  {
   if(!DailyOn() && !TotalOn())
     {
      Print("Prop Firm Guard: both limits are off, nothing to protect.");
      return INIT_PARAMETERS_INCORRECT;
     }
   if(InpResetHour < 0 || InpResetHour > 23)
     {
      Print("Prop Firm Guard: the reset hour must be between 0 and 23.");
      return INIT_PARAMETERS_INCORRECT;
     }
   if(InpSafetyMargin < 0.0 ||
      (DailyOn() && InpSafetyMargin >= InpMaxDailyLoss) ||
      (TotalOn() && InpSafetyMargin >= InpMaxDrawdown))
     {
      Print("Prop Firm Guard: the safety margin must be positive and smaller than each limit.");
      return INIT_PARAMETERS_INCORRECT;
     }

   g_trade.SetDeviationInPoints(50);
   g_trade.SetAsyncMode(false);

   EventSetTimer(1);
   Check();
   return INIT_SUCCEEDED;
  }

//+------------------------------------------------------------------+
//| Expert deinitialization                                          |
//+------------------------------------------------------------------+
void OnDeinit(const int reason)
  {
   EventKillTimer();
   if(g_ready)
      SaveState();
   Comment("");
  }

void OnTick()  { Check(); }
void OnTimer() { Check(); }
//+------------------------------------------------------------------+
