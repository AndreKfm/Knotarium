using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Features.Templates;
using Xunit;

namespace Knotarium.Tests.Templates;

public class TemplateParameterValidatorTests
{
    private static TemplateParameter Param(
        string key, string type = TemplateParameterTypes.String, bool required = true,
        string? @default = null, IReadOnlyList<string>? options = null)
        => new(key, key, null, type, options, @default, required);

    // ── Authoring (ValidateDeclarations) ──────────────────────────────────────

    [Fact]
    public void Declarations_reject_optional_parameter_without_a_default()
    {
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.ValidateDeclarations(new[] { Param("opt", required: false, @default: null) }));

        Assert.Contains(ex.Errors, e => e.Contains("opt", StringComparison.Ordinal) && e.Contains("default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Declarations_accept_optional_parameter_with_a_default()
    {
        var declared = TemplateParameterValidator.ValidateDeclarations(
            new[] { Param("opt", required: false, @default: "fallback") });

        Assert.Single(declared);
    }

    [Fact]
    public void Declarations_report_all_errors_at_once()
    {
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.ValidateDeclarations(new[]
            {
                Param("bad key"),                                          // invalid key
                Param("dup", required: false, @default: "x"),
                Param("dup", required: false, @default: "y"),              // duplicate
                Param("e", type: TemplateParameterTypes.Enum, required: false, @default: "a", options: null), // enum w/o options
            }));

        Assert.True(ex.Errors.Count >= 3);
    }

    [Fact]
    public void Declarations_reject_a_default_that_is_invalid_for_its_type()
    {
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.ValidateDeclarations(
                new[] { Param("n", type: TemplateParameterTypes.Number, required: false, @default: "not-a-number") }));

        Assert.Contains(ex.Errors, e => e.Contains("Default", StringComparison.Ordinal));
    }

    // ── Install/insert (Validate) ─────────────────────────────────────────────

    [Fact]
    public void Validate_uses_default_when_no_value_supplied()
    {
        var values = TemplateParameterValidator.Validate(
            new[] { Param("c", required: false, @default: "#default") }, rawValues: null);

        Assert.Equal("#default", values["c"].Text);
    }

    [Fact]
    public void Validate_required_missing_value_is_an_error()
    {
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.Validate(new[] { Param("c", required: true) }, rawValues: null));

        Assert.Contains(ex.Errors, e => e.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_coerces_number_and_boolean_to_typed_scalars()
    {
        var values = TemplateParameterValidator.Validate(
            new[] { Param("n", type: TemplateParameterTypes.Number), Param("b", type: TemplateParameterTypes.Boolean) },
            new Dictionary<string, string> { ["n"] = "42", ["b"] = "true" });

        Assert.Equal(42d, values["n"].Boxed);
        Assert.Equal(true, values["b"].Boxed);
    }

    [Fact]
    public void Validate_rejects_enum_value_outside_options()
    {
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.Validate(
                new[] { Param("mode", type: TemplateParameterTypes.Enum, options: new[] { "a", "b" }) },
                new Dictionary<string, string> { ["mode"] = "c" }));

        Assert.Contains(ex.Errors, e => e.Contains("mode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("slot:my-credential")] // a whole-value slot token the rebind pass would act on
    [InlineData("{{param:other}}")]    // a recognized parameter token
    public void Validate_rejects_values_carrying_a_live_slot_or_param_token(string injected)
    {
        // The injection guard: a parameter value must never carry a live slot:/{{param:…}} token, so the
        // later credential-rebind pass can't be tricked into rebinding it.
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.Validate(
                new[] { Param("c") }, new Dictionary<string, string> { ["c"] = injected }));

        Assert.Contains(ex.Errors, e => e.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://api.example.com/slot:statistics")] // "slot:" inside a larger string — not a token
    [InlineData("use {{ braces literally")]                  // an incomplete brace — not a token
    public void Validate_allows_token_lookalikes_that_the_rewriter_would_not_act_on(string value)
    {
        var values = TemplateParameterValidator.Validate(
            new[] { Param("v") }, new Dictionary<string, string> { ["v"] = value });

        Assert.Equal(value, values["v"].Text);
    }

    [Fact]
    public void Validate_reports_every_bad_value_before_throwing()
    {
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.Validate(
                new[]
                {
                    Param("n", type: TemplateParameterTypes.Number),
                    Param("b", type: TemplateParameterTypes.Boolean),
                },
                new Dictionary<string, string> { ["n"] = "x", ["b"] = "maybe" }));

        Assert.Equal(2, ex.Errors.Count);
    }

    [Fact]
    public void Validate_rejects_unknown_supplied_key()
    {
        var ex = Assert.Throws<TemplateParameterException>(() =>
            TemplateParameterValidator.Validate(
                Array.Empty<TemplateParameter>(), new Dictionary<string, string> { ["ghost"] = "x" }));

        Assert.Contains(ex.Errors, e => e.Contains("ghost", StringComparison.Ordinal));
    }
}
