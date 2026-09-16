namespace AuctionResponse.Core;

/// <summary>
/// Identities stamped into every event, transition and artifact. A change to any of these
/// must be a deliberate, versioned edit: replay compares them and refuses mismatched logs.
/// </summary>
public static class Versioning
{
    public const string SchemaVersion = "1.0.0";
    public const string EngineVersion = "1.0.0";

    /// <summary>Feature formula version. Bump when any Section 6-11 formula changes.</summary>
    public const string FeatureFormulaVersion = "1.0.0";
}
