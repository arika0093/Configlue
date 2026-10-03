using Amazon.Runtime;
using Amazon.S3;
using Configlue.Provider.Json;
using Configlue.Resource.S3;

namespace Configlue.Tests;

public sealed class ContextualSourceIdentityRegistrationTests
{
    [Test]
    public async Task S3SourcesWithDifferentKeysInOneBucketGetDistinctLogicalIds()
    {
        using var client = new AmazonS3Client(
            new BasicAWSCredentials("access-key", "secret-key"),
            new AmazonS3Config { ServiceURL = "http://localhost", ForcePathStyle = true }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromS3Object(CreateS3Options("one.json", client));
                    sources.FromS3Object(CreateS3Options("two.json", client));
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(2);
        sources.Select(static source => source.Id).Distinct().Count().ShouldBe(2);
        sources.All(static source => source.FixedResourceId is null).ShouldBeTrue();
    }

    [Test]
    public async Task JsonSectionViewsOfOneFileGetDistinctLogicalIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"configlue-{Guid.NewGuid():N}.json");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromJsonFile(CreateJsonOptions(path, "App:Settings"));
                    sources.FromJsonFile(CreateJsonOptions(path, "App:Other"));
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(2);
        sources.Select(static source => source.Id).Distinct().Count().ShouldBe(2);
        sources.Select(static source => source.PhysicalOrigin).Distinct().Count().ShouldBe(1);
    }

    private static S3ObjectSourceOptions CreateS3Options(string key, IAmazonS3 client) =>
        new()
        {
            BucketName = "settings",
            Key = key,
            Client = client,
            Codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>()),
            Writable = false,
            ResourceOptions = new S3ObjectResourceOptions
            {
                BucketNameSelector = RequireRoutedSubject,
                KeySelector = RequireRoutedSubjectKey,
            },
        };

    private static JsonFileSourceOptions CreateJsonOptions(string path, string sectionPath) =>
        new()
        {
            Path = path,
            SectionPath = sectionPath,
            ReadOnly = true,
            WatchChanges = false,
            ResourceOptions = new FileResourceOptions { CreateBackup = false },
        };

    private static string RequireRoutedSubject(ConfiglueResourceContext context) =>
        $"settings-{((RoutedSubject)context.Subject).Tenant}";

    private static string RequireRoutedSubjectKey(ConfiglueResourceContext context) =>
        $"{((RoutedSubject)context.Subject).Key.Value}/settings.json";

    private sealed record RoutedSubject(string Tenant) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Tenant);
    }
}
