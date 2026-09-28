using MediaForge.Core;

namespace MediaForge.Models;

public sealed class TimeSegment : ObservableObject
{
    private string _startHours = string.Empty;
    private string _startMinutes = string.Empty;
    private string _startSeconds = string.Empty;
    private string _endHours = string.Empty;
    private string _endMinutes = string.Empty;
    private string _endSeconds = string.Empty;

    public string StartHours { get => _startHours; set => SetProperty(ref _startHours, Digits(value)); }
    public string StartMinutes { get => _startMinutes; set => SetProperty(ref _startMinutes, Digits(value)); }
    public string StartSeconds { get => _startSeconds; set => SetProperty(ref _startSeconds, Digits(value)); }
    public string EndHours { get => _endHours; set => SetProperty(ref _endHours, Digits(value)); }
    public string EndMinutes { get => _endMinutes; set => SetProperty(ref _endMinutes, Digits(value)); }
    public string EndSeconds { get => _endSeconds; set => SetProperty(ref _endSeconds, Digits(value)); }

    public string? ToSection()
    {
        var all = new[] { StartHours, StartMinutes, StartSeconds, EndHours, EndMinutes, EndSeconds };
        if (all.All(string.IsNullOrWhiteSpace)) return null;
        string P(string value) => string.IsNullOrWhiteSpace(value) ? "00" : value.PadLeft(2, '0');
        return $"{P(StartHours)}:{P(StartMinutes)}:{P(StartSeconds)}-{P(EndHours)}:{P(EndMinutes)}:{P(EndSeconds)}";
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).Take(2).ToArray());
}
