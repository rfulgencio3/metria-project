using Metria.Api.Auth;
using Metria.Api.Contracts;
using Metria.Api.Data;
using Metria.Api.Models;
using Metria.API.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;

namespace Metria.Api.Endpoints;

public static class GoalsEndpoints
{
    public static WebApplication MapGoalsEndpoints(this WebApplication app)
    {
        const string tag = "Goals";
        var goals = app.MapGroup("/api/goals").WithTags(tag);
        var subGoals = goals.MapGroup("/{goalId:guid}/subgoals");

        goals.MapPost("", async (ClaimsPrincipal user, CreateGoalDto dto, AppDbContext db) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(dto.Text) || dto.Text.Length > 500)
            {
                return Results.BadRequest("Texto da meta e obrigatorio e deve ter no maximo 500 caracteres");
            }

            if (!Enum.IsDefined(typeof(GoalPeriod), dto.Period))
            {
                return Results.BadRequest("Periodo da meta invalido");
            }

            if (dto.StartDate >= dto.EndDate)
            {
                return Results.BadRequest("Data de inicio deve ser anterior a data de fim");
            }

            var now = DateTime.UtcNow;
            var hasActiveSubscription = await db.Subscriptions.AsNoTracking()
                .AnyAsync(s =>
                    s.UserId == account.Id &&
                    (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing) &&
                    s.CurrentPeriodEndUtc > now);

            if (!hasActiveSubscription)
            {
                var totalGoals = await db.Goals.AsNoTracking().CountAsync(g => g.UserId == account.Id && g.IsActive);
                if (totalGoals >= 5)
                {
                    return Results.StatusCode(402);
                }
            }

            var goal = new Goal
            {
                UserId = account.Id,
                Text = dto.Text.Trim(),
                Period = dto.Period,
                StartDate = ToUtc(dto.StartDate),
                EndDate = ToUtc(dto.EndDate),
                Category = dto.Category?.Trim(),
                Done = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                IsActive = true,
                UpdatedBy = email
            };

            db.Goals.Add(goal);
            await db.SaveChangesAsync();

            return Results.Created($"/api/goals/{goal.Id}", ToGoalDto(goal));
        }).RequireAuthorization();

        goals.MapGet("", async (ClaimsPrincipal user, AppDbContext db, string? period = null, string? startDate = null, string? endDate = null) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            var query = db.Goals.AsNoTracking().Where(g => g.UserId == account.Id && g.IsActive);

            if (!string.IsNullOrWhiteSpace(period) && Enum.TryParse<GoalPeriod>(period, true, out var goalPeriod))
            {
                query = query.Where(g => g.Period == goalPeriod);
            }

            if (!string.IsNullOrWhiteSpace(startDate))
            {
                if (DateTimeOffset.TryParse(startDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
                {
                    query = query.Where(g => g.StartDate >= start.UtcDateTime);
                }
                else if (DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDateOnly))
                {
                    query = query.Where(g => g.StartDate >= DateTime.SpecifyKind(startDateOnly, DateTimeKind.Utc));
                }
            }

            if (!string.IsNullOrWhiteSpace(endDate))
            {
                if (DateTimeOffset.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end))
                {
                    query = query.Where(g => g.EndDate <= end.UtcDateTime);
                }
                else if (DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDateOnly))
                {
                    query = query.Where(g => g.EndDate <= DateTime.SpecifyKind(endDateOnly, DateTimeKind.Utc));
                }
            }

            var list = await query.OrderByDescending(g => g.CreatedAtUtc).ToListAsync();
            return Results.Ok(list.Select(ToGoalDto).ToList());
        }).RequireAuthorization();

        goals.MapPut("/{id:guid}", async (ClaimsPrincipal user, Guid id, UpdateGoalDto dto, AppDbContext db) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id && g.UserId == account.Id && g.IsActive);
            if (goal is null) return Results.NotFound();

            goal.Done = dto.Done;
            goal.UpdatedAtUtc = DateTime.UtcNow;
            goal.UpdatedBy = email;

            await db.SaveChangesAsync();
            return Results.Ok(ToGoalDto(goal));
        }).RequireAuthorization();

        goals.MapDelete("/{id:guid}", async (ClaimsPrincipal user, Guid id, AppDbContext db) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == id && g.UserId == account.Id && g.IsActive);
            if (goal is null) return Results.NotFound();

            var hasActiveSubGoals = await db.SubGoals.AsNoTracking()
                .AnyAsync(sg => sg.GoalId == goal.Id && sg.IsActive);
            if (hasActiveSubGoals)
            {
                return Results.Conflict("Nao e possivel excluir uma meta que possui sub-metas ativas.");
            }

            goal.IsActive = false;
            goal.UpdatedAtUtc = DateTime.UtcNow;
            goal.UpdatedBy = email;

            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        subGoals.MapPost("", async (ClaimsPrincipal user, Guid goalId, CreateSubGoalDto dto, AppDbContext db) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            var goal = await db.Goals.AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == goalId && g.UserId == account.Id && g.IsActive);
            if (goal is null) return Results.NotFound("Meta principal nao encontrada");

            if (string.IsNullOrWhiteSpace(dto.Text) || dto.Text.Length > 300)
            {
                return Results.BadRequest("Texto da sub-meta e obrigatorio e deve ter no maximo 300 caracteres");
            }

            var start = ToUtc(dto.StartDate);
            var end = ToUtc(dto.EndDate);

            if (start >= end)
            {
                return Results.BadRequest("Data de inicio da sub-meta deve ser anterior a data de fim");
            }

            if (start < goal.StartDate || end > goal.EndDate)
            {
                return Results.BadRequest("Periodo da sub-meta deve estar dentro do periodo da meta principal");
            }

            var subGoal = new SubGoal
            {
                GoalId = goal.Id,
                Text = dto.Text.Trim(),
                StartDate = start,
                EndDate = end,
                Done = false,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                UpdatedBy = email
            };

            db.SubGoals.Add(subGoal);
            await db.SaveChangesAsync();

            return Results.Created($"/api/goals/{goalId}/subgoals/{subGoal.Id}", ToSubGoalDto(subGoal));
        }).RequireAuthorization();

        subGoals.MapGet("", async (ClaimsPrincipal user, Guid goalId, AppDbContext db) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            var goalExists = await db.Goals.AsNoTracking()
                .AnyAsync(g => g.Id == goalId && g.UserId == account.Id && g.IsActive);
            if (!goalExists) return Results.NotFound("Meta principal nao encontrada");

            var list = await db.SubGoals.AsNoTracking()
                .Where(sg => sg.GoalId == goalId && sg.IsActive)
                .OrderByDescending(sg => sg.CreatedAtUtc)
                .ToListAsync();

            return Results.Ok(list.Select(ToSubGoalDto).ToList());
        }).RequireAuthorization();

        subGoals.MapPut("/{subGoalId:guid}", async (ClaimsPrincipal user, Guid goalId, Guid subGoalId, UpdateSubGoalDto dto, AppDbContext db) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            var goal = await db.Goals.AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == goalId && g.UserId == account.Id && g.IsActive);
            if (goal is null) return Results.NotFound("Meta principal nao encontrada");

            var subGoal = await db.SubGoals
                .FirstOrDefaultAsync(sg => sg.Id == subGoalId && sg.GoalId == goalId && sg.IsActive);
            if (subGoal is null) return Results.NotFound("Sub-meta nao encontrada");

            if (dto.Text is not null)
            {
                var text = dto.Text.Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 300)
                {
                    return Results.BadRequest("Texto da sub-meta deve ter entre 1 e 300 caracteres");
                }
                subGoal.Text = text;
            }

            var updatedStart = dto.StartDate.HasValue ? ToUtc(dto.StartDate.Value) : subGoal.StartDate;
            var updatedEnd = dto.EndDate.HasValue ? ToUtc(dto.EndDate.Value) : subGoal.EndDate;

            if (updatedStart >= updatedEnd)
            {
                return Results.BadRequest("Data de inicio da sub-meta deve ser anterior a data de fim");
            }

            if (updatedStart < goal.StartDate || updatedEnd > goal.EndDate)
            {
                return Results.BadRequest("Periodo da sub-meta deve estar dentro do periodo da meta principal");
            }

            subGoal.StartDate = updatedStart;
            subGoal.EndDate = updatedEnd;

            if (dto.Done.HasValue)
            {
                subGoal.Done = dto.Done.Value;
            }

            subGoal.UpdatedAtUtc = DateTime.UtcNow;
            subGoal.UpdatedBy = email;

            await db.SaveChangesAsync();
            return Results.Ok(ToSubGoalDto(subGoal));
        }).RequireAuthorization();

        subGoals.MapDelete("/{subGoalId:guid}", async (ClaimsPrincipal user, Guid goalId, Guid subGoalId, AppDbContext db) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();

            var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (account is null) return Results.Unauthorized();

            var goalExists = await db.Goals.AsNoTracking()
                .AnyAsync(g => g.Id == goalId && g.UserId == account.Id && g.IsActive);
            if (!goalExists) return Results.NotFound("Meta principal nao encontrada");

            var subGoal = await db.SubGoals
                .FirstOrDefaultAsync(sg => sg.Id == subGoalId && sg.GoalId == goalId && sg.IsActive);
            if (subGoal is null) return Results.NotFound("Sub-meta nao encontrada");

            subGoal.IsActive = false;
            subGoal.UpdatedAtUtc = DateTime.UtcNow;
            subGoal.UpdatedBy = email;

            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private static GoalDto ToGoalDto(Goal goal) =>
        new(
            goal.Id,
            goal.Text,
            goal.Done,
            goal.Period.ToString(),
            goal.StartDate.ToString("yyyy-MM-dd"),
            goal.EndDate.ToString("yyyy-MM-dd"),
            goal.Category,
            goal.CreatedAtUtc.ToString("O")
        );

    private static SubGoalDto ToSubGoalDto(SubGoal subGoal) =>
        new(
            subGoal.Id,
            subGoal.GoalId,
            subGoal.Text,
            subGoal.Done,
            subGoal.StartDate.ToString("yyyy-MM-dd"),
            subGoal.EndDate.ToString("yyyy-MM-dd"),
            subGoal.CreatedAtUtc.ToString("O")
        );

    private static DateTime ToUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc) return value;
        if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
