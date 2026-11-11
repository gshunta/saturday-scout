using System.Text.Json;
using Xunit;

namespace SaturdayScout.Api.IntegrationTests;

public sealed class ApiContractArtifactTests
{
    [Fact]
    public void VersionedContractContainsPlayerRoutesAndDtosWithoutPrivateSigningMaterial()
    {
        var contractPath = FindRepositoryFile("docs/api/openapi-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(contractPath));
        var root = document.RootElement;

        Assert.StartsWith("3.", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal("1.0.0", root.GetProperty("info").GetProperty("version").GetString());
        var paths = root.GetProperty("paths");
        foreach (var path in new[]
                 {
                     "/api/v1/leagues/me",
                     "/api/v1/leagues/{leagueId}/slates/active",
                     "/api/v1/slates/{slateId}/picks/me",
                     "/api/v1/slates/{slateId}/picks",
                     "/api/v1/slates/{slateId}/submit",
                     "/api/v1/leagues/{leagueId}/leaderboard"
                 })
            Assert.True(paths.TryGetProperty(path, out _), $"Missing Player contract path: {path}");

        Assert.All(paths.EnumerateObject(), path => Assert.StartsWith("/api/v1/", path.Name, StringComparison.Ordinal));
        Assert.True(root.TryGetProperty("components", out var components));
        Assert.True(components.TryGetProperty("schemas", out var schemas));
        Assert.NotEmpty(schemas.EnumerateObject());

        var contractText = File.ReadAllText(contractPath);
        Assert.DoesNotContain("PRIVATE KEY", contractText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateKeyPath", contractText, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from the test output directory.");
    }
}
