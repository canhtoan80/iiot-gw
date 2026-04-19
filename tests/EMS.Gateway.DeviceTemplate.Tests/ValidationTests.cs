using EMS.Gateway.Contracts;
using EMS.Gateway.DeviceTemplate.Validators;
using FluentValidation.TestHelper;
using Xunit;

namespace EMS.Gateway.DeviceTemplate.Tests;

public class ValidationTests
{
    private readonly DeviceTemplateDtoValidator _validator = new();

    [Fact]
    public void Validation_ShouldFail_WhenPollCycleIsTooShort()
    {
        var model = CreateValidDevice() with { PollCycleMs = 100 };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.PollCycleMs);
    }

    [Fact]
    public void Validation_ShouldFail_WhenNCalcExpressionIsInvalid()
    {
        var model = CreateValidDevice() with 
        { 
            VirtualTagExpressions = new List<VirtualTagExpressionDto> 
            { 
                new("VT1", "1 + * 2", "unit", 0, 100) 
            } 
        };
        var result = _validator.TestValidate(model);
        result.ShouldHaveAnyValidationError();
    }

    [Fact]
    public void Validation_ShouldFail_WhenCommandWhitelistAddressIsMissingFromRegisters()
    {
        var model = CreateValidDevice() with 
        { 
            CommandWhitelist = new List<CommandWhitelistEntryDto> 
            { 
                new("C1", 999, "FC06", 0, 100, "desc") 
            } 
        };
        var result = _validator.TestValidate(model);
        result.ShouldHaveAnyValidationError();
    }

    private static DeviceTemplateDto CreateValidDevice()
    {
        return new DeviceTemplateDto(
            "device-01", "desc", ProtocolType.ModbusTCP, default, 1.0, 1.0, 1000, 60,
            new List<RegisterDefinitionDto> { new("T1", 100, "FC03", "float32", 1.0, "unit", 0, 0, 100, 0, 0, false) },
            new List<VirtualTagExpressionDto>(),
            new List<CommandWhitelistEntryDto>(),
            null, null, null
        );
    }
}
