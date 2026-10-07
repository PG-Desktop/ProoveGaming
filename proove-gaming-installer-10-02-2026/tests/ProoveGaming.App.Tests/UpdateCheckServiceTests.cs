using System;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// UpdateCheckService's three decision rules (docs/plans/contact-us-and-update-check). Static
// helpers like HomeViewModel.ContactUsFormValid: the instance seam (HTTP poll loop,
// PeriodicTimer cadence, UI-thread event) is the accepted manual-coverage boundary, so the
// tests drive the rules directly.
public class UpdateCheckServiceTests
{
	// ── TryParseManifest ──────────────────────────────────────────────

	[Fact]
	public void ParseManifest_FullManifest_AllFields()
	{
		var manifest = UpdateCheckService.TryParseManifest(
			"{\"version\":\"1.1.0\",\"download_url\":\"https://proovegaming.com/download\",\"release_notes\":\"test\"}");

		Assert.NotNull(manifest);
		Assert.Equal("1.1.0", manifest!.Version);
		Assert.Equal("https://proovegaming.com/download", manifest.DownloadUrl);
		Assert.Equal("test", manifest.ReleaseNotes);
	}

	[Fact]
	public void ParseManifest_MissingReleaseNotes_StillValid()
	{
		var manifest = UpdateCheckService.TryParseManifest(
			"{\"version\":\"1.1.0\",\"download_url\":\"https://proovegaming.com/download\"}");

		Assert.NotNull(manifest);
		Assert.Null(manifest!.ReleaseNotes);
	}

	[Theory]
	[InlineData("{\"download_url\":\"https://proovegaming.com/download\"}")]
	[InlineData("{\"version\":\"\",\"download_url\":\"https://proovegaming.com/download\"}")]
	[InlineData("{\"version\":\"   \",\"download_url\":\"https://proovegaming.com/download\"}")]
	public void ParseManifest_MissingOrBlankVersion_IsNull(string json)
	{
		Assert.Null(UpdateCheckService.TryParseManifest(json));
	}

	// download_url is required by the manifest contract too.
	[Fact]
	public void ParseManifest_MissingDownloadUrl_IsNull()
	{
		Assert.Null(UpdateCheckService.TryParseManifest("{\"version\":\"1.1.0\"}"));
	}

	// The PHP-router / error-page shapes: anything that is not JSON is a silent skip.
	[Theory]
	[InlineData("<html><body>404 Not Found</body></html>")]
	[InlineData("hello")]
	[InlineData("")]
	[InlineData("null")]
	public void ParseManifest_NonJsonOrNullBody_IsNull(string body)
	{
		Assert.Null(UpdateCheckService.TryParseManifest(body));
	}

	// Only an absolute https link is usable (docs/plans/fix-repository-audit-findings, A11): the
	// moment the announcement opens this link, http would let anyone on the network path swap the
	// download, and a file:, relative or script URL is never a download page.
	[Theory]
	[InlineData("http://proovegaming.com/download")]
	[InlineData("file:///C:/Windows/System32/cmd.exe")]
	[InlineData("javascript:alert(1)")]
	[InlineData("/download")]
	[InlineData("proovegaming.com/download")]
	public void ParseManifest_NonHttpsDownloadUrl_IsNull(string url)
	{
		var json = "{\"version\":\"1.1.0\",\"download_url\":\"" + url + "\"}";

		Assert.Null(UpdateCheckService.TryParseManifest(json));
	}

	[Fact]
	public void ParseManifest_UnknownExtraFields_Ignored()
	{
		var manifest = UpdateCheckService.TryParseManifest(
			"{\"version\":\"2.0\",\"download_url\":\"https://proovegaming.com/download\",\"sha256\":\"abc\",\"channel\":\"beta\"}");

		Assert.NotNull(manifest);
		Assert.Equal("2.0", manifest!.Version);
	}

	// ── IsNewerVersion ────────────────────────────────────────────────

	[Fact]
	public void NewerVersion_HigherManifest_IsTrue()
	{
		Assert.True(UpdateCheckService.IsNewerVersion(new Version("1.0.0"), "1.1.0"));
	}

	[Fact]
	public void NewerVersion_Equal_IsFalse()
	{
		Assert.False(UpdateCheckService.IsNewerVersion(new Version("1.1.0"), "1.1.0"));
	}

	[Fact]
	public void NewerVersion_Lower_IsFalse()
	{
		Assert.False(UpdateCheckService.IsNewerVersion(new Version("1.1.0"), "1.0.0"));
	}

	// System.Version treats missing components as lower, so mixed 2- and 4-part forms
	// compare in the intuitive order.
	[Fact]
	public void NewerVersion_TwoVsFourPartForms_CompareCorrectly()
	{
		Assert.True(UpdateCheckService.IsNewerVersion(new Version("1.0"), "1.0.0.1"));
		Assert.False(UpdateCheckService.IsNewerVersion(new Version(1, 0, 0, 1), "1.0"));
	}

	// An unparseable manifest version is never "newer" — the check skips silently.
	[Theory]
	[InlineData("latest")]
	[InlineData("v1.2")]
	[InlineData("")]
	[InlineData(null)]
	public void NewerVersion_UnparseableManifestVersion_IsFalse(string? manifestVersion)
	{
		Assert.False(UpdateCheckService.IsNewerVersion(new Version("1.0.0"), manifestVersion));
	}

	[Fact]
	public void NewerVersion_NoRunningVersion_IsFalse()
	{
		Assert.False(UpdateCheckService.IsNewerVersion(null, "1.1.0"));
	}

	// ── ShouldAnnounce ────────────────────────────────────────────────

	[Fact]
	public void Announce_FirstSighting_IsTrue()
	{
		Assert.True(UpdateCheckService.ShouldAnnounce(null, "1.1.0"));
	}

	[Fact]
	public void Announce_SameVersionAgain_IsFalse()
	{
		Assert.False(UpdateCheckService.ShouldAnnounce("1.1.0", "1.1.0"));
	}

	[Fact]
	public void Announce_DifferentVersionAfterAnnouncement_IsTrue()
	{
		Assert.True(UpdateCheckService.ShouldAnnounce("1.1.0", "1.2.0"));
	}
}
