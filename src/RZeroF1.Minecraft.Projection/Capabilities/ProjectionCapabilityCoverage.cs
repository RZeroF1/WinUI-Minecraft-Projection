namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionCapabilityCoverageIssue(string Code, string Key, string Detail);

internal sealed record ProjectionCapabilityCoverageReport(
    IReadOnlyList<ProjectionCapabilityCoverageIssue> Issues,
    IReadOnlyList<string> UncheckedChannels)
{
    internal bool Passed => Issues.Count == 0 && UncheckedChannels.Count == 0;
}

/// <summary>Compares an independently observed *complete* registry snapshot with the locked spec.</summary>
internal static class ProjectionCapabilityCoverage
{
    internal static ProjectionCapabilityCoverageReport Check(
        ProjectionVersionSelection selection,
        IEnumerable<ProjectionRendererRegistration> registrations,
        IEnumerable<string>? blockEntityTypes = null,
        IEnumerable<string>? resourceCodecs = null)
    {
        var issues = new List<ProjectionCapabilityCoverageIssue>();
        var uncheckedChannels = new List<string>();
        if (selection.Status != ProjectionVersionStatus.Verified || selection.Version is not { } version)
        {
            issues.Add(new(selection.Status.ToString(), selection.RequestedVersion, "No verified version profile; coverage is unknown."));
            return new(issues.AsReadOnly(), Array.AsReadOnly(new[] { "Registrations", "BlockEntityType", "ResourceCodec" }));
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            if (!observed.Add(registration.TypeId))
                issues.Add(new("DuplicateRegistration", registration.TypeId, "Repeated registration key."));
            if (!Enum.IsDefined(registration.Family) ||
                !ProjectionRenderCapabilityRegistry.Vanilla.Providers.ContainsKey(registration.Recipe))
                issues.Add(new("UnclassifiedMechanism", registration.TypeId, "Unknown mechanism family or recipe."));
            if (!version.Registrations.TryGetValue(registration.TypeId, out var expected))
                issues.Add(new("UnclassifiedRegistration", registration.TypeId, "New registration requires classification and evidence."));
            else if (expected != registration)
                issues.Add(new("RegistrationMismatch", registration.TypeId, "Provider, recipe or mechanism family differs from the verified declaration."));
        }
        foreach (var missing in version.Registrations.Keys.Except(observed).Order(StringComparer.Ordinal))
            issues.Add(new("MissingRegistration", missing, "Verified registration absent from observed census."));

        CheckSet(version.BlockEntityTypes, blockEntityTypes, "BlockEntityType");
        CheckSet(version.ResourceCodecs, resourceCodecs, "ResourceCodec");
        return new(issues.AsReadOnly(), uncheckedChannels.AsReadOnly());

        void CheckSet(IReadOnlySet<string>? expected, IEnumerable<string>? supplied, string kind)
        {
            if (supplied is null)
            {
                uncheckedChannels.Add(kind);
                return;
            }
            if (expected is null)
            {
                issues.Add(new("Unaudited" + kind, version.Id, "No verified census for this subsystem at this endpoint."));
                return;
            }
            var values = supplied.ToArray();
            var actual = values.ToHashSet(StringComparer.Ordinal);
            if (values.Length != actual.Count)
                issues.Add(new("Duplicate" + kind, version.Id, "Repeated census entry."));
            foreach (var unknown in actual.Except(expected).Order(StringComparer.Ordinal))
                issues.Add(new("Unclassified" + kind, unknown, "New subsystem entry requires an audited capability declaration."));
            foreach (var missing in expected.Except(actual).Order(StringComparer.Ordinal))
                issues.Add(new("Missing" + kind, missing, "Verified subsystem entry absent from observed census."));
        }
    }
}
