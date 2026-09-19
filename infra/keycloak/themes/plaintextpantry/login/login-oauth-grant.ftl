<#--
  The consent screen, written out rather than styled into shape.

  This is the page a person reads when they connect Plaintext Pantry to an
  assistant (Claude registers a client, sends them here, and asks for the
  scopes the F# server advertises). The stock template puts the client name in
  the same <h1> the sign-in page uses for the realm, lists privileges as a bare
  <ul>, and renders Yes/No as two identical full-width submits. None of that is
  reachable from CSS, so the markup is ours - the cost being that this file is
  pinned to this shape of Keycloak's consent screen (client name, scopes, two
  actions), which is its stable core.

  Kept from the original: the two <input type="submit"> named accept/cancel
  (Keycloak decides grant vs. deny by which one arrived), and advancedMsg on
  the client name and scope text so ${...} message keys still resolve.
-->
<#import "template.ftl" as layout>
<@layout.registrationLayout bodyClass="oauth"; section>
    <#if section = "header">
        <#-- The brand is drawn once, below, next to the question. -->
    <#elseif section = "form">
        <#assign appName><#if client.name?has_content>${advancedMsg(client.name)}<#else>${client.clientId}</#if></#assign>

        <div id="kc-oauth" class="pp-consent">
            <img class="pp-consent__logo" src="${url.resourcesPath}/img/logo.svg" alt="Plaintext Pantry">

            <h1 class="pp-consent__title"><strong>${appName}</strong> ${msg("ppConsentWants")}</h1>

            <p class="pp-consent__lead"><strong>${appName}</strong> ${msg("ppConsentLead")}</p>

            <ul class="pp-consent__list">
                <#if oauth.clientScopesRequested??>
                    <#list oauth.clientScopesRequested as clientScope>
                        <li class="pp-consent__item">
                            <#if !clientScope.parameterizedScopeParameter??>
                                ${advancedMsg(clientScope.consentScreenText)}
                            <#else>
                                ${advancedMsg(clientScope.consentScreenText)}: <b>${clientScope.parameterizedScopeParameter}</b>
                            </#if>
                        </li>
                    </#list>
                </#if>
            </ul>

            <p class="pp-consent__caution"><strong>${msg("ppConsentTrust")} ${appName}.</strong> ${msg("ppConsentCaution")}</p>

            <#if client.attributes.policyUri?? || client.attributes.tosUri??>
                <p class="pp-consent__links">
                    <#if client.attributes.tosUri??><a href="${client.attributes.tosUri}" target="_blank" rel="noopener">${msg("oauthGrantTos")}</a></#if>
                    <#if client.attributes.policyUri??><a href="${client.attributes.policyUri}" target="_blank" rel="noopener">${msg("oauthGrantPolicy")}</a></#if>
                </p>
            </#if>

            <form class="pp-consent__actions" action="${url.oauthAction}" method="POST">
                <input type="hidden" name="code" value="${oauth.code}">
                <input class="pp-btn pp-btn--primary" name="accept" id="kc-login" type="submit" value="${msg("ppConsentGrant")}"/>
                <input class="pp-btn pp-btn--ghost" name="cancel" id="kc-cancel" type="submit" value="${msg("ppConsentDeny")}"/>
            </form>
        </div>
    </#if>
</@layout.registrationLayout>
