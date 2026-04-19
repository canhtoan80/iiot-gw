using FluentValidation;
using EMS.Gateway.Contracts;

namespace EMS.Gateway.DeviceTemplate.Validators;

/// <summary>
/// Validator for RegisterDefinitionDto.
/// </summary>
public class RegisterDefinitionDtoValidator : AbstractValidator<RegisterDefinitionDto>
{
    public RegisterDefinitionDtoValidator()
    {
        RuleFor(x => x.TagName).NotEmpty();
        RuleFor(x => x.ScaleFactor).NotEqual(0);
        RuleFor(x => x.Deadband).GreaterThanOrEqualTo(0);
        RuleFor(x => x)
            .Must(x => x.RangeMin < x.RangeMax)
            .When(x => x.RangeMin != 0 || x.RangeMax != 0) // Simple check, assuming 0/0 means not set or we can use more complex logic
            .WithMessage("RangeMin must be less than RangeMax.");
        
        RuleFor(x => x.StuckTimeoutMs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.RocLimitPerSecond).GreaterThanOrEqualTo(0);
    }
}
