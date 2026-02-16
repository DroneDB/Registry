using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class PasswordPolicyValidatorTest
{
    #region No Policy Tests

    [Test]
    public void Validate_NoPolicyConfigured_ReturnsSuccess()
    {
        var validator = CreateValidator(null);

        var result = validator.Validate("a");

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Validate_NoPolicyConfigured_AcceptsEmptyPassword()
    {
        var validator = CreateValidator(null);

        var result = validator.Validate("");

        result.IsValid.ShouldBeTrue();
    }

    #endregion

    #region MinLength Tests

    [Test]
    public void Validate_MinLength_PasswordTooShort_ReturnsFailure()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 8 });

        var result = validator.Validate("short");

        result.IsValid.ShouldBeFalse();
        result.Errors.Length.ShouldBe(1);
        result.Errors[0].ShouldContain("at least 8 characters");
    }

    [Test]
    public void Validate_MinLength_PasswordExactLength_ReturnsSuccess()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 8 });

        var result = validator.Validate("12345678");

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_MinLength_PasswordLonger_ReturnsSuccess()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 8 });

        var result = validator.Validate("123456789");

        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_MinLength_NullPassword_ReturnsFailure()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 8 });

        var result = validator.Validate(null);

        result.IsValid.ShouldBeFalse();
        result.Errors[0].ShouldContain("at least 8 characters");
    }

    #endregion

    #region RequireDigit Tests

    [Test]
    public void Validate_RequireDigit_NoDigit_ReturnsFailure()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireDigit = true });

        var result = validator.Validate("abcdefgh");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("digit"));
    }

    [Test]
    public void Validate_RequireDigit_WithDigit_ReturnsSuccess()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireDigit = true });

        var result = validator.Validate("abcdefg1");

        result.IsValid.ShouldBeTrue();
    }

    #endregion

    #region RequireUppercase Tests

    [Test]
    public void Validate_RequireUppercase_NoUppercase_ReturnsFailure()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireUppercase = true });

        var result = validator.Validate("alllowercase");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("uppercase"));
    }

    [Test]
    public void Validate_RequireUppercase_WithUppercase_ReturnsSuccess()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireUppercase = true });

        var result = validator.Validate("hasUpperCase");

        result.IsValid.ShouldBeTrue();
    }

    #endregion

    #region RequireLowercase Tests

    [Test]
    public void Validate_RequireLowercase_NoLowercase_ReturnsFailure()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireLowercase = true });

        var result = validator.Validate("ALLUPPERCASE");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("lowercase"));
    }

    [Test]
    public void Validate_RequireLowercase_WithLowercase_ReturnsSuccess()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireLowercase = true });

        var result = validator.Validate("HASLOWERcase");

        result.IsValid.ShouldBeTrue();
    }

    #endregion

    #region RequireNonAlphanumeric Tests

    [Test]
    public void Validate_RequireNonAlphanumeric_NoSpecialChar_ReturnsFailure()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireNonAlphanumeric = true });

        var result = validator.Validate("OnlyLettersAndDigits123");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("special character"));
    }

    [Test]
    public void Validate_RequireNonAlphanumeric_WithSpecialChar_ReturnsSuccess()
    {
        var validator = CreateValidator(new PasswordPolicy { MinLength = 1, RequireNonAlphanumeric = true });

        var result = validator.Validate("Has@Special");

        result.IsValid.ShouldBeTrue();
    }

    #endregion

    #region Combined Policy Tests

    [Test]
    public void Validate_AllRequirements_AllMet_ReturnsSuccess()
    {
        var validator = CreateValidator(new PasswordPolicy
        {
            MinLength = 8,
            RequireDigit = true,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireNonAlphanumeric = true
        });

        var result = validator.Validate("Str0ng@Pass");

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Validate_AllRequirements_NoneMetExceptLength_ReturnsMultipleErrors()
    {
        var validator = CreateValidator(new PasswordPolicy
        {
            MinLength = 4,
            RequireDigit = true,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireNonAlphanumeric = true
        });

        // All lowercase, no digits, no uppercase, no special chars
        var result = validator.Validate("abcdef");

        result.IsValid.ShouldBeFalse();
        result.Errors.Length.ShouldBe(3); // missing digit, uppercase, nonalphanumeric
    }

    [Test]
    public void Validate_AllRequirements_TooShort_ReturnsAllErrors()
    {
        var validator = CreateValidator(new PasswordPolicy
        {
            MinLength = 20,
            RequireDigit = true,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireNonAlphanumeric = true
        });

        var result = validator.Validate("abc");

        result.IsValid.ShouldBeFalse();
        result.Errors.Length.ShouldBeGreaterThanOrEqualTo(3); // too short + missing digit + missing uppercase + missing nonalphanumeric
    }

    [Test]
    public void Validate_DefaultPolicyValues_MinLength8Only_ReturnsCorrectResult()
    {
        // Default PasswordPolicy: MinLength=8, all Require* are false
        var validator = CreateValidator(new PasswordPolicy());

        var shortResult = validator.Validate("short");
        shortResult.IsValid.ShouldBeFalse();

        var longResult = validator.Validate("longenough");
        longResult.IsValid.ShouldBeTrue();
    }

    #endregion

    #region Helper

    private static PasswordPolicyValidator CreateValidator(PasswordPolicy policy)
    {
        var appSettings = new AppSettings
        {
            PasswordPolicy = policy,
            DefaultAdmin = new AdminInfo { UserName = "admin", Password = "admin" }
        };
        var options = new Mock<IOptions<AppSettings>>();
        options.Setup(x => x.Value).Returns(appSettings);

        return new PasswordPolicyValidator(options.Object);
    }

    #endregion
}
