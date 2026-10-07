using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ProoveGaming.App.Tests;

// LanguageResourceParityTests only catches a key added to ONE language — if the Information
// page title were dropped from BOTH resource files, parity would stay green while the header
// rendered its raw DynamicResource fallback. These pin that the key exists with real text,
// and that the view actually resolves its title and back plate through the shared resources
// instead of a hardcoded string or a forked geometry.
public class InformationViewResourcesTests
{
	[Theory]
	[InlineData("Strings.en.axaml")]
	[InlineData("Strings.uk.axaml")]
	public void TitleKeyIsPresentWithText(string fileName)
	{
		var match = Regex.Match(ReadCopiedFile(fileName),
			@"x:Key=""InformationView_Title"">([^<]*)<");

		Assert.True(match.Success, $"InformationView_Title is missing from {fileName}.");
		Assert.False(string.IsNullOrWhiteSpace(match.Groups[1].Value),
			$"InformationView_Title in {fileName} has no text.");
	}

	[Theory]
	[InlineData("Strings.en.axaml", "InformationView_Tab_PrivacyPolicy")]
	[InlineData("Strings.en.axaml", "InformationView_Tab_TermsOfService")]
	[InlineData("Strings.en.axaml", "InformationView_Tab_LicenseAgreement")]
	[InlineData("Strings.uk.axaml", "InformationView_Tab_PrivacyPolicy")]
	[InlineData("Strings.uk.axaml", "InformationView_Tab_TermsOfService")]
	[InlineData("Strings.uk.axaml", "InformationView_Tab_LicenseAgreement")]
	public void TabKeyIsPresentWithText(string fileName, string key)
	{
		var match = Regex.Match(ReadCopiedFile(fileName),
			$@"x:Key=""{Regex.Escape(key)}"">([^<]*)<");

		Assert.True(match.Success, $"{key} is missing from {fileName}.");
		Assert.False(string.IsNullOrWhiteSpace(match.Groups[1].Value),
			$"{key} in {fileName} has no text.");
	}

	[Fact]
	public void ViewBindsTitleViaDynamicResource()
	{
		Assert.Contains("{DynamicResource InformationView_Title}",
			ReadCopiedFile("InformationView.axaml"));
	}

	[Fact]
	public void ViewBindsAllTabLabelsViaDynamicResource()
	{
		var view = ReadCopiedFile("InformationView.axaml");

		Assert.Contains("{DynamicResource InformationView_Tab_PrivacyPolicy}", view);
		Assert.Contains("{DynamicResource InformationView_Tab_TermsOfService}", view);
		Assert.Contains("{DynamicResource InformationView_Tab_LicenseAgreement}", view);
	}

	// The sign-up agreement sentence links each document separately; the keys must exist
	// in both languages and the view must wire each link to its own command.
	[Theory]
	[InlineData("Strings.en.axaml", "WelcomeView_Terms_TermsOfService")]
	[InlineData("Strings.en.axaml", "WelcomeView_Terms_PrivacyPolicy")]
	[InlineData("Strings.en.axaml", "WelcomeView_Terms_License")]
	[InlineData("Strings.uk.axaml", "WelcomeView_Terms_TermsOfService")]
	[InlineData("Strings.uk.axaml", "WelcomeView_Terms_PrivacyPolicy")]
	[InlineData("Strings.uk.axaml", "WelcomeView_Terms_License")]
	public void SignUpLinkKeyIsPresentWithText(string fileName, string key)
	{
		var match = Regex.Match(ReadCopiedFile(fileName),
			$@"x:Key=""{Regex.Escape(key)}"">([^<]*)<");

		Assert.True(match.Success, $"{key} is missing from {fileName}.");
		Assert.False(string.IsNullOrWhiteSpace(match.Groups[1].Value),
			$"{key} in {fileName} has no text.");
	}

	[Fact]
	public void SignUpLinksOpenDistinctDocuments()
	{
		var view = ReadCopiedFile("WelcomeView.axaml");

		Assert.Contains("{Binding OpenTermsOfServiceCommand}", view);
		Assert.Contains("{Binding OpenPrivacyPolicyCommand}", view);
		Assert.Contains("{Binding OpenLicenseAgreementCommand}", view);
	}

	// One title + one tail marker per document: a swapped-language or wrong-document
	// transcription fails the title check, a truncated one fails the tail check.
	[Theory]
	[InlineData("PrivacyPolicyEnView.axaml", "Privacy Policy", "16. Contact Us")]
	[InlineData("PrivacyPolicyUkView.axaml", "Політика конфіденційності", "16. Зв’яжіться з нами")]
	[InlineData("TermsOfServiceEnView.axaml", "Terms of Service", "18. Contact Information")]
	[InlineData("TermsOfServiceUkView.axaml", "Умови надання послуг", "18. Контактна інформація")]
	[InlineData("LicenseAgreementEnView.axaml", "End User License and Sublicense Agreement", "16. Contact Information")]
	[InlineData("LicenseAgreementUkView.axaml", "Ліцензійна та субліцензійна угода кінцевого користувача", "16. Контактна інформація")]
	public void ContentViewCarriesItsDocument(string fileName, string title, string lastHeading)
	{
		var view = ReadCopiedFile(fileName);

		Assert.Contains($"<Run Classes=\"legalDocTitle\" Text=\"{title}\"", view);
		Assert.Contains(lastHeading, view);
	}

	[Fact]
	public void ViewUsesSharedBackPlateGeometry()
	{
		Assert.Contains("{StaticResource CutCornerFrameGeometry}",
			ReadCopiedFile("InformationView.axaml"));
	}

	// The installer's agreement step carries the same generated Privacy Policy (its own
	// Run classes); same title+tail guard against a truncated or language-swapped copy.
	[Theory]
	[InlineData("AgreementTermsEnView.axaml", "Privacy Policy", "16. Contact Us")]
	[InlineData("AgreementTermsUkView.axaml", "Політика конфіденційності", "16. Зв’яжіться з нами")]
	public void InstallerAgreementViewCarriesPrivacyPolicy(string fileName, string title, string lastHeading)
	{
		var view = ReadCopiedFile(fileName);

		Assert.Contains($"<Run Classes=\"agreementDocTitleText\" Text=\"{title}\"", view);
		Assert.Contains(lastHeading, view);
	}

	// Every legal document renders its support email as a clickable inline link.
	[Theory]
	[InlineData("PrivacyPolicyEnView.axaml")]
	[InlineData("PrivacyPolicyUkView.axaml")]
	[InlineData("TermsOfServiceEnView.axaml")]
	[InlineData("TermsOfServiceUkView.axaml")]
	[InlineData("LicenseAgreementEnView.axaml")]
	[InlineData("LicenseAgreementUkView.axaml")]
	[InlineData("AgreementTermsEnView.axaml")]
	[InlineData("AgreementTermsUkView.axaml")]
	public void EmailRendersAsLink(string fileName)
	{
		var view = ReadCopiedFile(fileName);

		Assert.Contains("Classes=\"legalDocEmailLink\"", view);
		Assert.Contains("Content=\"support@proovegaming.com\"", view);
		Assert.Contains("Click=\"OnEmailLinkClicked\"", view);
		// The address must never remain as plain run text.
		Assert.DoesNotMatch("<Run [^>]*support@proovegaming\\.com", view);
	}

	// The docx sources render the standalone company-registration line in italics in
	// the Privacy Policy and License Agreement (and Terms of Service has no italics at
	// all) — pinned so a re-extraction/regeneration cannot silently drop the styling.
	[Theory]
	[InlineData("PrivacyPolicyEnView.axaml", true)]
	[InlineData("PrivacyPolicyUkView.axaml", true)]
	[InlineData("LicenseAgreementEnView.axaml", true)]
	[InlineData("LicenseAgreementUkView.axaml", true)]
	[InlineData("AgreementTermsEnView.axaml", true)]
	[InlineData("AgreementTermsUkView.axaml", true)]
	[InlineData("TermsOfServiceEnView.axaml", false)]
	[InlineData("TermsOfServiceUkView.axaml", false)]
	public void RegistrationLineItalicMatchesTheDocx(string fileName, bool italic)
	{
		var view = ReadCopiedFile(fileName);

		if (italic)
			Assert.Matches("<Run [^>]*FontStyle=\"Italic\"[^>]*Text=\"BE FIRST COMPANY LIMITED", view);
		else
			Assert.DoesNotContain("FontStyle=\"Italic\"", view);
	}

	private static string ReadCopiedFile(string fileName)
	{
		var path = Path.Combine(AppContext.BaseDirectory, fileName);
		Assert.True(File.Exists(path),
			$"{fileName} was not copied to the test output ({path}). " +
			"Check the <None Include=...> items in ProoveGaming.App.Tests.csproj.");
		return File.ReadAllText(path);
	}
}
