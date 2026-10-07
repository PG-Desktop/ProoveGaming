using System;
using Avalonia.Controls;
using ProoveGaming.App.Models;
using ProoveGaming.Common.Localization;

namespace ProoveGaming.App.Views.Information;

// Shared by InformationView and AgreementTermsView: resolves the per-language content
// view for a legal document at the current app language.
public static class LegalDocumentContent
{
	public static Control CreateView(LegalDocument document)
	{
		var isUkrainian = string.Equals(
			LocalizationManager.CurrentLanguageCode,
			LocalizationConstants.UkrainianLanguageCode,
			StringComparison.OrdinalIgnoreCase);

		return document switch
		{
			LegalDocument.TermsOfService => isUkrainian
				? new TermsOfServiceUkView()
				: (Control)new TermsOfServiceEnView(),
			LegalDocument.LicenseAgreement => isUkrainian
				? new LicenseAgreementUkView()
				: (Control)new LicenseAgreementEnView(),
			_ => isUkrainian
				? new PrivacyPolicyUkView()
				: (Control)new PrivacyPolicyEnView(),
		};
	}

	// The sign-up agreement step's header title key. Deliberately NOT the
	// InformationView_Tab_* keys: those are baked-uppercase tab chips, while App page
	// titles render in normal case (PR #131 review m1).
	public static string TitleResourceKey(LegalDocument document) => document switch
	{
		LegalDocument.TermsOfService => "AgreementTermsView_Title_TermsOfService",
		LegalDocument.LicenseAgreement => "AgreementTermsView_Title_LicenseAgreement",
		_ => "AgreementTermsView_Title_PrivacyPolicy",
	};
}
