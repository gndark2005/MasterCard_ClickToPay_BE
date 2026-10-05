using System.Text.Json;

namespace PowerTranz3DSecurePoc.Demo;

/// <summary>
/// Mastercard Unified Checkout (Click to Pay) page, loaded in the checkout page's iframe from the demo origin.
/// Flow: init() → getCards() (recognized consumer) or authenticate() by email → card list → checkoutWithCard().
/// On COMPLETE it hands srcCorrelationId and the merchant-transaction-id header to the merchant app through
/// <see cref="CheckoutPage.ActionFunctionName"/>; the merchant server then calls Mastercard /checkout (via
/// MasterCard_ClickToPay_BE), decrypts the payload and continues with PowerTranz.
/// Same flow as powertranz/hpp/index.html, with checkoutWithCard() wired to the payment.
/// </summary>
public static class ClickToPayPage
{
    public const string PagePath = "/hpp/ClickToPay.html";

    public static string Render(ClickToPayOptions options, decimal amount, string currency)
    {
        var data = JsonSerializer.Serialize(new
        {
            srcDpaId = options.SrcDpaId,
            dpaName = options.DpaName,
            cardBrands = options.CardBrands,
            email = options.DefaultEmail,
            amount = decimal.Round(amount, 2),
            currency,
            action = CheckoutPage.ActionFunctionName,
        });
        var sdk = JsonSerializer.Serialize($"{options.SdkUrl}?srcDpaId={Uri.EscapeDataString(options.SrcDpaId)}&locale=en_US");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Click to Pay</title>
            <script>document.write('<script src=' + {{sdk}} + '><\/script>');</script>
            <script type="module" src="https://src.mastercard.com/srci/integration/components/src-ui-kit/src-ui-kit.esm.js"></script>
            <link rel="stylesheet" href="https://src.mastercard.com/srci/integration/components/src-ui-kit/src-ui-kit.css">
            <style>
              :root { --ink: #1f1f1f; --muted: #6b6b6b; --line: #e3e3e3; --teal: #0f7f78; --bad: #cf222e; }
              * { box-sizing: border-box; }
              [hidden] { display: none !important; }
              body { margin: 0; padding: 20px; color: var(--ink); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; background: #fff; }
              h2 { margin: 0 0 4px; font-size: 20px; font-weight: 600; }
              .sub { margin: 0 0 18px; color: var(--muted); font-size: 13px; }
              .step { max-width: 560px; }
              label { display: block; margin-bottom: 4px; font-size: 13px; font-weight: 600; }
              input[type=email] { width: 100%; padding: 10px 12px; border: 1px solid #c9c9c9; border-radius: 8px; font: inherit; }
              .primary { margin-top: 14px; width: 100%; padding: 11px; border: 0; border-radius: 20px; background: var(--teal); color: #fff;
                         font-size: 15px; font-weight: 600; cursor: pointer; }
              .primary:disabled { background: #9fbfbc; cursor: default; }
              .link { border: 0; background: none; padding: 0; color: var(--teal); font: inherit; font-size: 13px; cursor: pointer; text-decoration: underline; }
              .loading { display: flex; flex-direction: column; align-items: center; gap: 12px; padding: 48px 0; color: var(--muted); }
              .error { margin: 0 0 14px; padding: 10px 12px; border-radius: 8px; background: #fff5f5; border: 1px solid #ffc9c9; color: var(--bad); font-size: 14px; }
              #authFrame { width: 100%; height: 520px; border: 1px solid var(--line); border-radius: 10px; margin-top: 12px; }
              .amount { font-weight: 600; color: var(--ink); }
            </style>
            </head>
            <body>
              <h2>Click to Pay</h2>
              <p class="sub">Mastercard Unified Checkout · <span class="amount" id="amount"></span></p>
              <p class="error" id="error" hidden></p>

              <div class="step" id="stepLoading">
                <div class="loading"><src-loader locale="en_US"></src-loader><span id="loadingText">Connecting to Click to Pay…</span></div>
              </div>

              <div class="step" id="stepIdentify" hidden>
                <label for="email">Email address</label>
                <input type="email" id="email" autocomplete="email">
                <button type="button" class="primary" id="continue">Continue</button>
              </div>

              <div class="step" id="stepCards" hidden>
                <src-card-list id="cardList" locale="en_US" card-selection-type="radioButton" display-sign-out display-header="false"></src-card-list>
                <button type="button" class="primary" id="pay" disabled>Pay with Click to Pay</button>
                <p><button type="button" class="link" id="otherEmail">Use another email</button></p>
              </div>

              <!-- Mastercard renders its own UI (OTP, card confirmation) here: the windowRef of authenticate()/checkoutWithCard(). -->
              <iframe id="authFrame" name="authFrame" title="Click to Pay" allow="publickey-credentials-get *" hidden></iframe>

            <script>
            (() => {
              const data = {{data}};
              const $ = id => document.getElementById(id);
              const send = payload => window[data.action](JSON.stringify(payload));
              const log = text => { try { send({ action: 'clickToPayLog', text }); } catch { /* console only */ } };
              const authFrame = $('authFrame');
              let selectedCard = null;

              $('amount').textContent = data.amount.toFixed(2) + ' ' + data.currency;
              $('email').value = data.email || '';

              function show(step, text) {
                for (const id of ['stepLoading', 'stepIdentify', 'stepCards']) $(id).hidden = id !== step;
                if (text) $('loadingText').textContent = text;
              }
              function error(message, detail) {
                $('error').textContent = message;
                $('error').hidden = !message;
                if (message) log('ERROR ' + message + (detail ? ' — ' + JSON.stringify(detail) : ''));
              }
              // Mastercard's hosted UI (OTP, etc.) only appears when it navigates the windowRef iframe.
              function resetAuthFrame() { authFrame.hidden = true; authFrame.src = 'about:blank'; }
              authFrame.addEventListener('load', () => {
                try { if (authFrame.contentWindow.location.href === 'about:blank') return; } catch { /* cross-origin = Mastercard UI */ }
                authFrame.hidden = false;
              });
              const describe = e => e && (e.reason || e.message || e.error || JSON.stringify(e));

              async function showCards(cards) {
                if (!cards || cards.length === 0) {
                  show('stepIdentify');
                  error('No Click to Pay cards found for this consumer. Try another email.');
                  return;
                }
                log(cards.length + ' Click to Pay card(s) available.');
                selectedCard = null;
                $('pay').disabled = true;
                // src-card-list comes from an ES module, so it may not be defined yet.
                await customElements.whenDefined('src-card-list');
                $('cardList').loadCards(cards);
                show('stepCards');
              }

              async function start() {
                error('');
                resetAuthFrame();
                show('stepLoading', 'Connecting to Click to Pay…');
                if (typeof MastercardCheckoutServices === 'undefined') {
                  error('The Mastercard Click to Pay SDK could not be loaded.');
                  return;
                }
                window.mcCheckoutServices = window.mcCheckoutServices || new MastercardCheckoutServices();
                try {
                  log('init() srcDpaId=' + data.srcDpaId);
                  const init = await window.mcCheckoutServices.init({
                    srcDpaId: data.srcDpaId,
                    cardBrands: data.cardBrands,
                    checkoutExperience: 'WITHIN_CHECKOUT',
                    dpaData: { dpaName: data.dpaName, dpaPresentationName: data.dpaName },
                    dpaTransactionOptions: {
                      dpaLocale: 'en_US',
                      dpaBillingPreference: 'FULL',
                      dpaShippingPreference: 'NONE',
                      consumerNameRequested: true,
                      consumerEmailAddressRequested: true,
                      consumerPhoneNumberRequested: true,
                      threeDsPreference: 'NONE',
                      confirmPayment: false,
                      paymentOptions: [{ dpaDynamicDataTtlMinutes: 15, dynamicDataType: 'CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM' }],
                      transactionAmount: { transactionAmount: data.amount, transactionCurrencyCode: data.currency },
                    },
                  });
                  log('init() OK: ' + JSON.stringify(init));
                  show('stepLoading', 'Looking for your Click to Pay cards…');
                  const cards = await window.mcCheckoutServices.getCards();
                  log('getCards(): ' + (cards ? cards.length : 0) + ' card(s) (recognized consumer)');
                  if (cards && cards.length) await showCards(cards); else show('stepIdentify');
                } catch (e) {
                  show('stepIdentify');
                  error('Click to Pay could not start: ' + describe(e), e);
                }
              }

              async function identify() {
                const email = $('email').value.trim();
                if (!email) { error('Enter your email address.'); return; }
                error('');
                show('stepLoading', 'Click to Pay is finding your cards…');
                try {
                  log('authenticate() EMAIL_ADDRESS');
                  const result = await window.mcCheckoutServices.authenticate({
                    windowRef: authFrame.contentWindow,
                    accountReference: { consumerIdentity: { identityType: 'EMAIL_ADDRESS', identityValue: email } },
                    requestRecognitionToken: false,
                  });
                  resetAuthFrame();
                  log('authenticate() result: ' + (result && result.authenticationStatus || 'OK'));
                  await showCards(result && result.cards);
                } catch (e) {
                  resetAuthFrame();
                  show('stepIdentify');
                  error('Click to Pay sign-in failed: ' + describe(e), e);
                }
              }

              async function pay() {
                if (!selectedCard) return;
                error('');
                show('stepLoading', 'Confirming with Click to Pay…');
                try {
                  log('checkoutWithCard() srcDigitalCardId=' + selectedCard);
                  const result = await window.mcCheckoutServices.checkoutWithCard({
                    srcDigitalCardId: selectedCard,
                    windowRef: authFrame.contentWindow,
                    checkoutExperience: 'WITHIN_CHECKOUT',
                    dpaTransactionOptions: {
                      paymentOptions: [{ dpaDynamicDataTtlMinutes: 15, dynamicDataType: 'CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM' }],
                    },
                    recognitionTokenRequested: false,
                  });
                  resetAuthFrame();
                  const code = result && result.checkoutActionCode;
                  log('checkoutWithCard() checkoutActionCode=' + code);
                  if (code === 'COMPLETE') {
                    const headers = result.headers || {};
                    const correlationId = result.checkoutResponseData && result.checkoutResponseData.srcCorrelationId;
                    const merchantTransactionId = headers['merchant-transaction-id'];
                    if (!correlationId || !merchantTransactionId) {
                      show('stepCards');
                      error('Click to Pay did not return srcCorrelationId / merchant-transaction-id.');
                      return;
                    }
                    show('stepLoading', 'Processing your payment…');
                    send({ action: 'clickToPay', correlationId, merchantTransactionId, flowId: headers['x-src-cx-flow-id'] || null });
                  } else if (code === 'CHANGE_CARD' || code === 'CANCEL') {
                    show('stepCards');
                  } else {
                    show('stepCards');
                    error('Click to Pay checkout ended with ' + code + '.', result);
                  }
                } catch (e) {
                  resetAuthFrame();
                  show('stepCards');
                  error('Click to Pay checkout failed: ' + describe(e), e);
                }
              }

              $('continue').onclick = identify;
              $('email').addEventListener('keydown', e => { if (e.key === 'Enter') identify(); });
              $('pay').onclick = pay;
              $('otherEmail').onclick = () => { error(''); show('stepIdentify'); };
              $('cardList').addEventListener('selectSrcDigitalCardId', e => { selectedCard = e.detail; $('pay').disabled = !selectedCard; });
              $('cardList').addEventListener('clickSignOutLink', async () => {
                try { await window.mcCheckoutServices.signOut(); } catch (e) { log('signOut() failed: ' + describe(e)); }
                show('stepIdentify');
              });

              if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
            })();
            </script>
            </body>
            </html>
            """;
    }
}
