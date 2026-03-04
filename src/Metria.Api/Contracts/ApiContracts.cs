using Metria.API.Models.Enums;

namespace Metria.Api.Contracts;

public record SignupDto(string Name, string Email, string Password);
public record LoginDto(string Email, string Password);
public record AssessmentDto(Dictionary<string, int> Scores, double Average, string CreatedAtUtc);
public record GoalDto(Guid Id, string Text, bool Done, string Period, string StartDate, string EndDate, string? Category, string CreatedAtUtc);
public record CreateGoalDto(string Text, GoalPeriod Period, DateTime StartDate, DateTime EndDate, string? Category);
public record UpdateGoalDto(bool Done);
public record UpdatePreferencesDto(string? Name, string? BirthDate);
public record CheckoutReq(string PriceId, string? SuccessUrl, string? CancelUrl);
public record PortalReq(string? ReturnUrl);
public record SyncReq(string? SubscriptionId, string? CustomerId, string? Email, string? CheckoutSessionId);
