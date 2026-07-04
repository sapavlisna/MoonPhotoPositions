using System.Globalization;
using MoonApp.Core;

namespace MoonApp.Maui;

public partial class DatePickerPage : ContentPage
{
    static readonly CultureInfo Cz = CultureInfo.GetCultureInfo("cs-CZ");
    static readonly string[] WeekDays = ["Po", "Út", "St", "Čt", "Pá", "So", "Ne"];

    readonly TaskCompletionSource<DateTime?> _tcs = new();
    DateOnly _selected;
    DateOnly _month;        // první den zobrazeného měsíce
    DateOnly _fullMoon;     // úplněk zobrazeného měsíce
    readonly DateOnly _today;

    public DatePickerPage(DateTime selected)
    {
        InitializeComponent();
        _selected = DateOnly.FromDateTime(selected);
        _today = DateOnly.FromDateTime(DateTime.Today);
        _month = new DateOnly(_selected.Year, _selected.Month, 1);
        BuildWeekHeader();
        Render();
    }

    public Task<DateTime?> PickAsync() => _tcs.Task;

    void BuildWeekHeader()
    {
        for (int i = 0; i < 7; i++)
        {
            var l = new Label
            {
                Text = WeekDays[i],
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center,
                TextColor = Col("#64748B", "#94A3B0"),
            };
            WeekHeader.Add(l, i, 0);
        }
    }

    void Render()
    {
        MonthLbl.Text = Cap(_month.ToString("MMMM yyyy", Cz));
        _fullMoon = Astro.NextFullMoon(_month);   // úplněk na/po 1. dni měsíce
        ApplyBtn.Text = $"Použít · {_selected.ToString("ddd d. M. yyyy", Cz)}";
        DaysGrid.Children.Clear();
        DaysGrid.RowDefinitions.Clear();
        for (int r = 0; r < 6; r++) DaysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // pondělí prvního zobrazeného týdne
        int lead = ((int)_month.DayOfWeek + 6) % 7;   // Po=0
        var start = _month.AddDays(-lead);

        for (int i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            DaysGrid.Add(DayCell(d), i % 7, i / 7);
        }
    }

    View DayCell(DateOnly d)
    {
        bool inMonth = d.Month == _month.Month;
        bool sel = d == _selected;
        bool today = d == _today;
        var (frac, phase) = Astro.MoonPhase(d);
        bool full = inMonth && d == _fullMoon;

        var num = new Label
        {
            Text = d.Day.ToString(),
            FontSize = 15,
            FontAttributes = sel ? FontAttributes.Bold : FontAttributes.None,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = sel ? Colors.White
                : !inMonth ? Col("#B4BDC9", "#4A5666")
                : Col("#1E293B", "#E6EDF5"),
        };
        var glyph = new Label
        {
            Text = PhaseGlyph(phase, frac),
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            Opacity = inMonth ? 1 : 0.45,
        };

        var stack = new VerticalStackLayout
        {
            Spacing = 0,
            Padding = new Thickness(0, 6),
            HorizontalOptions = LayoutOptions.Fill,
            Children = { num, glyph },
        };

        var border = new Border
        {
            StrokeThickness = (today && !sel) || (full && inMonth && !sel) ? 1.5 : 0,
            Stroke = sel ? Colors.Transparent
                : today ? Col("#2563EB", "#4F8EF7")
                : full ? Col("#93C5FD", "#3B5678")
                : Colors.Transparent,
            BackgroundColor = sel ? Col("#2563EB", "#4F8EF7") : Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = 0,
            Content = stack,
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _selected = d;
            if (!inMonth) _month = new DateOnly(d.Year, d.Month, 1);
            Render();
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }

    // 🌑🌒🌓🌔🌕🌖🌗🌘 podle fáze (0/1 nov, 0.5 úplněk)
    static string PhaseGlyph(double phase, double frac)
    {
        if (frac <= 0.04) return "🌑";
        if (frac >= 0.96) return "🌕";
        bool waxing = phase < 0.5;
        if (frac < 0.35) return waxing ? "🌒" : "🌘";
        if (frac < 0.65) return waxing ? "🌓" : "🌗";
        return waxing ? "🌔" : "🌖";
    }

    void OnPrevMonth(object? sender, EventArgs e) { _month = _month.AddMonths(-1); Render(); }
    void OnNextMonth(object? sender, EventArgs e) { _month = _month.AddMonths(1); Render(); }

    void OnToday(object? sender, EventArgs e) => Select(_today);
    void OnTomorrow(object? sender, EventArgs e) => Select(_today.AddDays(1));
    void OnFullMoon(object? sender, EventArgs e) => Select(Astro.NextFullMoon(_today));

    void Select(DateOnly d)
    {
        _selected = d;
        _month = new DateOnly(d.Year, d.Month, 1);
        Render();
    }

    async void OnApply(object? sender, EventArgs e)
    {
        _tcs.TrySetResult(_selected.ToDateTime(TimeOnly.MinValue));
        await Navigation.PopModalAsync();
    }

    async void OnBack(object? sender, EventArgs e)
    {
        _tcs.TrySetResult(null);
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        _tcs.TrySetResult(null);
        return base.OnBackButtonPressed();
    }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Cz) + s[1..];

    static Color Col(string light, string dark) =>
        Color.FromArgb(Application.Current?.RequestedTheme == AppTheme.Dark ? dark : light);
}
