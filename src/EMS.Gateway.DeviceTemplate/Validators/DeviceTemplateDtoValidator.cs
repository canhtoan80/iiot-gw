using FluentValidation;
using EMS.Gateway.Contracts;
using NCalc2;

namespace EMS.Gateway.DeviceTemplate.Validators;

/// <summary>
/// Validator for DeviceTemplateDto.
/// </summary>
public class DeviceTemplateDtoValidator : AbstractValidator<DeviceTemplateDto>
{
    public DeviceTemplateDtoValidator()
    {
        RuleFor(x => x.DeviceId)
            .NotEmpty()
            .Matches(@"^[a-z0-9-]{1,64}$")
            .WithMessage("DeviceId must be alphanumeric lowercase with hyphens, 1-64 characters.");

        RuleFor(x => x.PollCycleMs).GreaterThanOrEqualTo(500);
        RuleFor(x => x.CtRatio).GreaterThan(0);
        RuleFor(x => x.PtRatio).GreaterThan(0);

        RuleForEach(x => x.Registers).SetValidator(new RegisterDefinitionDtoValidator());

        RuleForEach(x => x.VirtualTagExpressions).Custom((expr, context) =>
        {
            try
            {
                var ncalcExpr = new Expression(expr.Expression);
                if (ncalcExpr.HasErrors())
                {
                    context.AddFailure($"Virtual tag '{expr.TagName}' expression has syntax errors: {ncalcExpr.Error}");
                }
            }
            catch (Exception ex)
            {
                context.AddFailure($"Virtual tag '{expr.TagName}' expression compilation failed: {ex.Message}");
            }
        });

        RuleFor(x => x)
            .Must(x => x.CommandWhitelist.All(c => x.Registers.Any(r => r.Address == c.RegisterAddress)))
            .WithMessage("Command whitelist register addresses must exist in registers list.");
    }
}
