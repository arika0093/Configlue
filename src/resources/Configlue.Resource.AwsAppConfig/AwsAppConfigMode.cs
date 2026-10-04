namespace Configlue.Resource.AwsAppConfig;

/// <summary>How an AppConfig resource retrieves its configuration profile payload.</summary>
public enum AwsAppConfigMode
{
    /// <summary>Uses the AppConfig Data API session model directly.</summary>
    Direct,

    /// <summary>Uses the local AppConfig Agent endpoint.</summary>
    Agent,
}
