namespace Hl7Receiver.Hl7;

/// <summary>
/// The strict-vs-lenient knobs. Defaults reflect what a radiology report needs to be safely usable.
/// </summary>
public sealed record ValidationPolicy(
    IReadOnlySet<string> AcceptedMessageTypes,
    bool RequireSendingFacility = true,          // MSH-4: without it we can't attribute the report to a provider or de-duplicate safely
    bool RequirePatientIdentifier = true,        // PID-3.1
    bool RequirePatientName = true,              // PID-5.1
    bool RequireAccessionNumber = true,          // links the report to the imaging study
    bool RequireAtLeastOneObservation = true,    // an OBR with no OBX has no report content
    bool RequireObservationResultStatus = true)  // OBX-11 is HL7-required and is our truncation detector
{
    public static readonly ValidationPolicy Default = new(
        AcceptedMessageTypes: new HashSet<string>(StringComparer.Ordinal) { "ORU^R01" });
}

/// <summary>
/// Per-provider configuration: which fields carry which meaning, and how strict to be.
/// This is the seam for "every provider has quirks". Woodbine gets <see cref="Default"/> until we learn otherwise.
/// </summary>
public sealed record ProviderProfile(
    string Name,
    ValidationPolicy Policy,
    FieldRef AccessionNumber,   // on OBR
    FieldRef PatientIdentifier) // on PID (first repetition)
{
    public static readonly ProviderProfile Default = new(
        Name: "default",
        Policy: ValidationPolicy.Default,
        AccessionNumber: new FieldRef(3, 1),   // OBR-3.1 filler order number — ASSUMPTION, confirm with Woodbine
        PatientIdentifier: new FieldRef(3, 1)); // PID-3.1 — ASSUMPTION (MRN), confirm with Woodbine
}

/// <summary>Resolves the profile for a sender (MSH-4 sending facility).</summary>
public interface IProviderProfileRegistry
{
    ProviderProfile For(string? sendingFacility);
}

/// <summary>
/// In-memory registry. Overrides are keyed by MSH-4. Today there are none: Woodbine's samples fit the default,
/// and Maya couldn't yet tell us the other three providers' quirks. When they arrive, they go here (or move to
/// configuration/DB) — nothing else in the pipeline needs to change.
/// </summary>
public sealed class ProviderProfileRegistry : IProviderProfileRegistry
{
    private readonly IReadOnlyDictionary<string, ProviderProfile> _overrides;
    private readonly ProviderProfile _default;

    public ProviderProfileRegistry(IReadOnlyDictionary<string, ProviderProfile>? overrides = null, ProviderProfile? defaultProfile = null)
    {
        _overrides = overrides ?? new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase);
        _default = defaultProfile ?? ProviderProfile.Default;
    }

    public ProviderProfile For(string? sendingFacility) =>
        sendingFacility is not null && _overrides.TryGetValue(sendingFacility, out var profile) ? profile : _default;
}
