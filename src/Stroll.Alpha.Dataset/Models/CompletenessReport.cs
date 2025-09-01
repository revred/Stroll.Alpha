namespace Stroll.Alpha.Dataset.Models;

public sealed record CompletenessReport(
    string Symbol,
    DateOnly Session,
    int StrikesLeft,
    int StrikesRight,
    double Score,
    string[] Warnings
);