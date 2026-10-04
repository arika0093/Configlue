namespace Configlue;

/// <summary>
/// Marks a model member as holding sensitive data such as a credential, token, or key.
/// </summary>
/// <remarks>
/// <para>
/// Generic diagnostics and tooling must not expose the value of a marked member by default.
/// Typed application access still returns the real value; this attribute is not an
/// access-control boundary.
/// </para>
/// <para>
/// Persistence and storage behavior remain controlled by the selected source, resource,
/// codec, and transformer. Marking a member does not skip serialization, encrypt the value,
/// route it to a secret-manager source, or ignore the member.
/// </para>
/// <para>
/// When a structural or nested member is marked, its entire subtree is treated as sensitive
/// by generic tooling. When a collection member is marked, its element values are treated as
/// sensitive.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field,
    Inherited = true,
    AllowMultiple = false
)]
public sealed class SecretValueAttribute : Attribute;
