namespace KnotGarden.Features.Condition;

/// <summary>The shipped legacy operator vocabulary. Kept so legacy <c>left/operator/right</c> nodes
/// still run; mapped to the new OperatorId space by <see cref="LegacyConditionMap"/> (B6).</summary>
public enum ConditionOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Contains
}
