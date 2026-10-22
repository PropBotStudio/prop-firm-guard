// Prop Firm Guard for cTrader - daily loss and max drawdown protection
// Version 1.01 - PropBot Studio (https://propbotstudio.fr/en/)
//
// Watches the account equity every second. When the daily loss limit or the
// maximum drawdown is reached, it closes every open position, cancels every
// pending order and (optionally) closes any new trade while the limit is hit.
// It never opens trades.

using System;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum DailyReference
    {
        Balance,
        HigherOfBalanceAndEquity
    }

    public enum DrawdownMode
    {
        Static,
        Trailing
    }

    public enum ResetTimeZone
    {
        UTC,
        NewYork,
        London,
        CentralEurope
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class PropFirmGuard : Robot
    {
        // ---------------------------------------------------------------- inputs
        [Parameter("Account size (0 = current balance)", Group = "Account", DefaultValue = 0, MinValue = 0)]
        public double AccountSize { get; set; }

        [Parameter("Max daily loss (%, 0 = off)", Group = "Limits", DefaultValue = 5, MinValue = 0, MaxValue = 100)]
        public double MaxDailyLoss { get; set; }

        [Parameter("Max total drawdown (%, 0 = off)", Group = "Limits", DefaultValue = 10, MinValue = 0, MaxValue = 100)]
        public double MaxDrawdown { get; set; }

        [Parameter("Safety margin: act earlier by (%)", Group = "Limits", DefaultValue = 0, MinValue = 0, MaxValue = 100)]
        public double SafetyMargin { get; set; }

        [Parameter("Daily loss reference", Group = "Limits", DefaultValue = DailyReference.Balance)]
        public DailyReference DailyRef { get; set; }

        [Parameter("Drawdown mode", Group = "Limits", DefaultValue = DrawdownMode.Static)]
        public DrawdownMode DdMode { get; set; }

        [Parameter("Reset hour (0-23)", Group = "Daily reset", DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int ResetHour { get; set; }

        [Parameter("Reset time zone", Group = "Daily reset", DefaultValue = ResetTimeZone.UTC)]
        public ResetTimeZone ResetZone { get; set; }

        [Parameter("Keep closing new trades while a limit is hit", Group = "Behaviour", DefaultValue = true)]
        public bool BlockAfterHit { get; set; }

        [Parameter("Warn when this % of a limit is used (0 = off)", Group = "Behaviour", DefaultValue = 80, MinValue = 0, MaxValue = 100)]
        public double WarnAt { get; set; }

        [Parameter("Email alerts to (empty = off)", Group = "Behaviour", DefaultValue = "")]
        public string EmailTo { get; set; }

        [Parameter("Reset saved state on start", Group = "Behaviour", DefaultValue = false)]
        public bool ResetState { get; set; }

        // ---------------------------------------------------------------- state
        private const LocalStorageScope Scope = LocalStorageScope.Device;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private string _prefix = "";
        private double _size;
        private double _dayRef;
        private string _dayKey = "";
        private double _peak;
        private bool _dailyHit;
        private bool _totalHit;
        private bool _warnedDaily;
        private bool _warnedTotal;
        private bool _mustFlatten;
        private bool _busy;
        private bool _ready;
        private DateTime _lastFailLog = DateTime.MinValue;

        private Border _border;
        private TextBlock _panel;

        // ---------------------------------------------------------------- helpers
        private bool DailyOn { get { return MaxDailyLoss > 0; } }
        private bool TotalOn { get { return MaxDrawdown > 0; } }

        private double DailyLimit { get { return _size * (MaxDailyLoss - SafetyMargin) / 100.0; } }
        private double TotalLimit { get { return _size * (MaxDrawdown - SafetyMargin) / 100.0; } }
        private double TotalRef { get { return DdMode == DrawdownMode.Trailing ? _peak : _size; } }
        private double DailyFloor { get { return _dayRef - DailyLimit; } }
        private double TotalFloor { get { return TotalRef - TotalLimit; } }

        private static double UsedPct(double loss, double limit)
        {
            if (limit <= 0) return 0;
            return Math.Max(0, loss / limit * 100.0);
        }

        private bool IsRealTime { get { return RunningMode == RunningMode.RealTime; } }

        // ---------------------------------------------------------------- time zones
        private static DateTime NthSunday(int year, int month, int n)
        {
            var d = new DateTime(year, month, 1);
            while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
            return d.AddDays(7 * (n - 1));
        }

        private static DateTime LastSunday(int year, int month)
        {
            var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
            return d;
        }

        // offset of the selected time zone from UTC, daylight saving included
        private TimeSpan ZoneOffset(DateTime utc)
        {
            int y = utc.Year;
            switch (ResetZone)
            {
                case ResetTimeZone.NewYork:
                {
                    // DST: second Sunday of March 2:00 local -> first Sunday of November 2:00 local
                    DateTime start = NthSunday(y, 3, 2).AddHours(7);
                    DateTime end = NthSunday(y, 11, 1).AddHours(6);
                    return TimeSpan.FromHours(utc >= start && utc < end ? -4 : -5);
                }
                case ResetTimeZone.London:
                case ResetTimeZone.CentralEurope:
                {
                    // EU summer time: last Sunday of March 1:00 UTC -> last Sunday of October 1:00 UTC
                    DateTime start = LastSunday(y, 3).AddHours(1);
                    DateTime end = LastSunday(y, 10).AddHours(1);
                    bool summer = utc >= start && utc < end;
                    int std = ResetZone == ResetTimeZone.London ? 0 : 1;
                    return TimeSpan.FromHours(std + (summer ? 1 : 0));
                }
                default:
                    return TimeSpan.Zero;
            }
        }

        private DateTime ZoneNow()
        {
            DateTime utc = Server.TimeInUtc;
            return utc + ZoneOffset(utc);
        }

        // start of the trading day containing "local", with the reset hour applied
        private DateTime TradingDayStart(DateTime local)
        {
            return local.AddHours(-ResetHour).Date.AddHours(ResetHour);
        }

        private string DayKey(DateTime local)
        {
            return TradingDayStart(local).ToString("yyyy-MM-dd HH", Inv);
        }

        private string ZoneName
        {
            get
            {
                switch (ResetZone)
                {
                    case ResetTimeZone.NewYork: return "New York time";
                    case ResetTimeZone.London: return "London time";
                    case ResetTimeZone.CentralEurope: return "Central European time";
                    default: return "UTC";
                }
            }
        }

        // ---------------------------------------------------------------- saved state
        // cTrader keys: latin letters, digits and inner spaces only
        private string Key(string name) { return _prefix + name; }

        private bool LoadDouble(string name, out double value)
        {
            value = 0;
            string s = LocalStorage.GetString(Key(name), Scope);
            return !string.IsNullOrEmpty(s) &&
                   double.TryParse(s, NumberStyles.Float, Inv, out value);
        }

        private string LoadString(string name)
        {
            return LocalStorage.GetString(Key(name), Scope) ?? "";
        }

        private void SaveDouble(string name, double value)
        {
            LocalStorage.SetString(Key(name), value.ToString("R", Inv), Scope);
        }

        private void SaveState()
        {
            if (_prefix == "") return;
            SaveDouble("size", _size);
            SaveDouble("dayRef", _dayRef);
            LocalStorage.SetString(Key("dayKey"), _dayKey, Scope);
            SaveDouble("peak", _peak);
            SaveDouble("dailyHit", _dailyHit ? 1 : 0);
            SaveDouble("totalHit", _totalHit ? 1 : 0);
            LocalStorage.Flush(Scope);
        }

        private void ClearState()
        {
            foreach (var name in new[] { "size", "dayRef", "dayKey", "peak", "dailyHit", "totalHit" })
                LocalStorage.SetString(Key(name), "", Scope);
            LocalStorage.Flush(Scope);
        }

        private void StartNewDay(string key)
        {
            _dayKey = key;
            _dayRef = DailyRef == DailyReference.HigherOfBalanceAndEquity
                ? Math.Max(Account.Balance, Account.Equity)
                : Account.Balance;
            _dailyHit = false;
            _warnedDaily = false;
            SaveState();
        }

        // ---------------------------------------------------------------- messages
        private void Notify(string text)
        {
            string msg = "Prop Firm Guard: " + text;
            Print(msg);
            if (!IsRealTime) return;

            try { Notifications.ShowPopup("Prop Firm Guard", text, PopupNotificationState.Error); }
            catch (Exception) { }

            if (!string.IsNullOrWhiteSpace(EmailTo))
            {
                try { Notifications.SendEmail(EmailTo.Trim(), EmailTo.Trim(), "Prop Firm Guard alert", msg); }
                catch (Exception) { }
            }
        }

        private void LogFailure(string what, long id, string error)
        {
            if ((DateTime.UtcNow - _lastFailLog).TotalSeconds < 30) return;
            _lastFailLog = DateTime.UtcNow;
            Print("Prop Firm Guard: could not {0} #{1} ({2}). Retrying.", what, id, error);
        }

        // ---------------------------------------------------------------- close everything
        // returns true when nothing is left open
        private bool CloseEverything()
        {
            bool done = true;

            foreach (var p in Positions.ToArray())
            {
                long id = p.Id;
                string symbol = p.SymbolName;
                double lots = p.Quantity;
                var r = ClosePosition(p);
                if (r.IsSuccessful)
                    Print("Prop Firm Guard: closed position #{0} ({1}, {2} lots).", id, symbol, lots.ToString("0.##", Inv));
                else
                {
                    done = false;
                    LogFailure("close position", id, r.Error.HasValue ? r.Error.Value.ToString() : "unknown error");
                }
            }

            foreach (var o in PendingOrders.ToArray())
            {
                long id = o.Id;
                string symbol = o.SymbolName;
                var r = CancelPendingOrder(o);
                if (r.IsSuccessful)
                    Print("Prop Firm Guard: cancelled pending order #{0} ({1}).", id, symbol);
                else
                {
                    done = false;
                    LogFailure("cancel order", id, r.Error.HasValue ? r.Error.Value.ToString() : "unknown error");
                }
            }

            return done && Positions.Count == 0 && PendingOrders.Count == 0;
        }

        // ---------------------------------------------------------------- panel
        private void CreatePanel()
        {
            if (RunningMode == RunningMode.Optimization) return;

            _panel = new TextBlock
            {
                FontSize = 13,
                FontFamily = "Consolas",
                ForegroundColor = Color.White,
                Text = "Prop Firm Guard\nStarting..."
            };
            _border = new Border
            {
                Child = _panel,
                BackgroundColor = Color.FromArgb(215, 18, 22, 32),
                BorderColor = Color.FromArgb(255, 76, 141, 255),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Chart.AddControl(_border);
        }

        private void UpdatePanel(double equity)
        {
            if (_panel == null) return;

            string status = "ACTIVE";
            Color accent = Color.FromArgb(255, 46, 204, 113);
            if (_totalHit)
            {
                status = "BLOCKED - max drawdown reached";
                accent = Color.FromArgb(255, 231, 76, 60);
            }
            else if (_dailyHit)
            {
                status = "PAUSED - daily loss limit reached";
                accent = Color.FromArgb(255, 243, 156, 18);
            }

            string text = "PROP FIRM GUARD\n";
            text += "Status:      " + status + "\n";
            text += string.Format(Inv, "Equity:      {0:F2}   Account size: {1:F2}\n", equity, _size);
            if (DailyOn)
                text += string.Format(Inv, "Daily loss:  {0:F2} / {1:F2} ({2:F0}% used)   Floor: {3:F2}\n",
                    Math.Max(0, _dayRef - equity), DailyLimit, UsedPct(_dayRef - equity, DailyLimit), DailyFloor);
            if (TotalOn)
                text += string.Format(Inv, "Drawdown:    {0:F2} / {1:F2} ({2:F0}% used)   Floor: {3:F2}\n",
                    Math.Max(0, TotalRef - equity), TotalLimit, UsedPct(TotalRef - equity, TotalLimit), TotalFloor);
            DateTime nextReset = TradingDayStart(ZoneNow()).AddDays(1);
            text += "Next reset:  " + nextReset.ToString("yyyy-MM-dd HH:mm", Inv) + " (" + ZoneName + ")";

            _panel.Text = text;
            _border.BorderColor = accent;
        }

        // ---------------------------------------------------------------- start / stop
        protected override void OnStart()
        {
            if (!DailyOn && !TotalOn)
            {
                Print("Prop Firm Guard: both limits are off, nothing to protect.");
                Stop();
                return;
            }
            if ((DailyOn && SafetyMargin >= MaxDailyLoss) || (TotalOn && SafetyMargin >= MaxDrawdown))
            {
                Print("Prop Firm Guard: the safety margin must be smaller than each limit.");
                Stop();
                return;
            }

            _prefix = string.Format(Inv, "PFG {0} {1} ", Account.Number, Account.IsLive ? "live" : "demo");

            double v;
            if (ResetState || (LoadDouble("size", out v) && v <= 0))
                ClearState();

            // account size
            if (AccountSize > 0)
                _size = AccountSize;
            else if (LoadDouble("size", out v) && v > 0)
                _size = v;
            else
                _size = Account.Balance;

            // highest equity for the trailing drawdown
            if (LoadDouble("peak", out v) && v > 0)
                _peak = v;
            else
                _peak = Math.Max(_size, Account.Equity);

            // locks from a previous session
            _dailyHit = LoadDouble("dailyHit", out v) && v > 0.5;
            _totalHit = LoadDouble("totalHit", out v) && v > 0.5;

            // same trading day as last session: keep its reference, otherwise start a new day
            string today = DayKey(ZoneNow());
            double savedRef;
            if (LoadString("dayKey") == today && LoadDouble("dayRef", out savedRef) && savedRef > 0)
            {
                _dayKey = today;
                _dayRef = savedRef;
            }
            else
                StartNewDay(today);

            SaveState();
            _ready = true;

            Positions.Opened += OnPositionOpened;
            PendingOrders.Created += OnPendingOrderCreated;

            CreatePanel();
            Print("Prop Firm Guard: started on account {0} ({1}), account size {2}.",
                Account.Number, Account.BrokerName, _size.ToString("F2", Inv));

            Timer.Start(TimeSpan.FromSeconds(1));
            Check();
        }

        protected override void OnStop()
        {
            Positions.Opened -= OnPositionOpened;
            PendingOrders.Created -= OnPendingOrderCreated;
            if (_ready) SaveState();
        }

        protected override void OnTick() { Check(); }
        protected override void OnTimer() { Check(); }

        private void OnPositionOpened(PositionOpenedEventArgs args) { Check(); }
        private void OnPendingOrderCreated(PendingOrderCreatedEventArgs args) { Check(); }

        // ---------------------------------------------------------------- main check
        private void Check()
        {
            if (!_ready || _busy) return;
            _busy = true;
            try
            {
                // new trading day: new reference, daily lock released
                string today = DayKey(ZoneNow());
                if (today != _dayKey)
                {
                    StartNewDay(today);
                    Print("Prop Firm Guard: new trading day, daily loss reference {0}.", _dayRef.ToString("F2", Inv));
                }

                double equity = Account.Equity;
                if (equity <= 0) return;

                // trailing drawdown follows the highest equity
                if (DdMode == DrawdownMode.Trailing && equity > _peak)
                {
                    _peak = equity;
                    SaveDouble("peak", _peak);
                }

                // limits
                if (DailyOn && !_dailyHit && equity <= DailyFloor)
                {
                    _dailyHit = true;
                    _mustFlatten = true;
                    SaveState();
                    Notify(string.Format(Inv,
                        "daily loss limit reached (equity {0:F2}, floor {1:F2}). All trades are closed until the daily reset.",
                        equity, DailyFloor));
                }
                if (TotalOn && !_totalHit && equity <= TotalFloor)
                {
                    _totalHit = true;
                    _mustFlatten = true;
                    SaveState();
                    Notify(string.Format(Inv,
                        "maximum drawdown reached (equity {0:F2}, floor {1:F2}). Trading is blocked. Restart with 'Reset saved state' to unlock.",
                        equity, TotalFloor));
                }

                // early warnings
                if (WarnAt > 0)
                {
                    if (DailyOn && !_dailyHit)
                    {
                        double used = UsedPct(_dayRef - equity, DailyLimit);
                        if (!_warnedDaily && used >= WarnAt)
                        {
                            _warnedDaily = true;
                            Notify(string.Format(Inv, "{0:F0}% of the daily loss limit used.", used));
                        }
                        else if (_warnedDaily && used < WarnAt / 2.0)
                            _warnedDaily = false;
                    }
                    if (TotalOn && !_totalHit)
                    {
                        double used = UsedPct(TotalRef - equity, TotalLimit);
                        if (!_warnedTotal && used >= WarnAt)
                        {
                            _warnedTotal = true;
                            Notify(string.Format(Inv, "{0:F0}% of the maximum drawdown used.", used));
                        }
                        else if (_warnedTotal && used < WarnAt / 2.0)
                            _warnedTotal = false;
                    }
                }

                // enforcement
                bool locked = _dailyHit || _totalHit;
                if (_mustFlatten || (locked && BlockAfterHit))
                {
                    if (Positions.Count > 0 || PendingOrders.Count > 0)
                    {
                        if (CloseEverything())
                            _mustFlatten = false;
                    }
                    else
                        _mustFlatten = false;
                }

                UpdatePanel(Account.Equity);
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
