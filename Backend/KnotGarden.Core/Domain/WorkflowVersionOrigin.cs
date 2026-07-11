namespace KnotGarden.Core.Domain;

/// <summary>
/// Describes how a <see cref="WorkflowVersion"/> came to exist.
/// </summary>
public enum WorkflowVersionOrigin
{
    /// <summary>
    /// The version was created by publishing the workflow's current draft.
    /// </summary>
    Published,

    /// <summary>
    /// The version was created by copying an earlier version forward (restore).
    /// </summary>
    Restored,

    /// <summary>
    /// The version was created by importing an external definition file.
    /// </summary>
    Imported
}
