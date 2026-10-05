using System.Globalization;
using System.Text.Json;
using PowerTranz3DSecurePoc.Shop;

namespace PowerTranz3DSecurePoc.Demo;

/// <summary>
/// Checkout page shown after the cart. It stays loaded while the shopper retries with different cards:
/// <list type="bullet">
/// <item>the iframe shows the PowerTranz HPP checkout template (<see cref="FormPath"/>, unmodified); its POST is
/// captured by the app and turned into a Sale — or, when a Hosted Payment Page is configured, a button starts the
/// Sale and PowerTranz serves the page itself;</item>
/// <item>the Sale RedirectData then runs in the same iframe (3DS challenge) via <c>window.showRedirect</c>;</item>
/// <item>each attempt ends in a success/error popup via <c>window.showOutcome</c>;</item>
/// <item>a live console polls <see cref="PullLogsFunctionName"/> for the PowerTranz calls being made.</item>
/// </list>
/// Served from the demo origin so the page can fill the template's amount and test cards (same origin).
/// </summary>
public static class CheckoutPage
{
    public const string ActionFunctionName = "__checkoutAction";
    public const string PullLogsFunctionName = "__pullLogs";
    public const string PagePath = "/checkout";
    public const string FormPath = "/hpp/Checkout.html";

    /// <param name="clickToPay">Adds the "Click to Pay" method (Mastercard Unified Checkout in the iframe).</param>
    public static string Render(Order order, ShopOptions shop, string currencyCode, bool hostedPage, bool clickToPay, int logStart)
    {
        // A future expiry (MMYY, as the template asks) keeps the test cards valid whenever the demo runs.
        var expiration = DateTime.UtcNow.AddYears(2).ToString("MMyy", CultureInfo.InvariantCulture);

        // The default System.Text.Json encoder escapes <, > and &, so this is safe inside <script>.
        var data = JsonSerializer.Serialize(new
        {
            storeName = shop.StoreName,
            symbol = shop.CurrencySymbol,
            currencyCode,
            lines = order.Lines.Select(l => new { name = l.Product.Name, emoji = l.Product.Emoji, price = l.Product.Price, qty = l.Quantity }),
            subtotal = order.Subtotal,
            shipping = order.Shipping,
            total = order.Total,
            hostedPage,
            clickToPay,
            clickToPayUrl = ClickToPayPage.PagePath,
            formUrl = FormPath,
            logStart,
            action = ActionFunctionName,
            pull = PullLogsFunctionName,
            testCards = new[]
            {
                new { label = "Mastercard 5115 •••• 0001", pan = "5115010000000001", expiration, cvv = "123" },
                new { label = "Visa 4012 •••• 0006 (3DS challenge)", pan = "4012000000020006", expiration, cvv = "323" },
            },
        });

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Checkout</title>
            <style>
              :root { --ink: #1f1f1f; --muted: #6b6b6b; --line: #e3e3e3; --teal: #0f7f78; --teal-dark: #0b645f;
                      --summary: #f3f6f8; --page: #f2f2f2; --ok: #1a7f37; --bad: #cf222e; }
              * { box-sizing: border-box; }
              [hidden] { display: none !important; }
              button { font-family: inherit; }
              body { margin: 0; background: var(--page); color: var(--ink); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
              .topbar { background: #141414; color: #fff; padding: 18px 16px; }
              .topbar .inner { max-width: 1180px; margin: 0 auto; display: flex; align-items: center; gap: 10px; font-size: 22px; }
              .shell { max-width: 1180px; margin: 0 auto; background: #fff; box-shadow: 0 1px 3px rgba(0,0,0,.06); }
              .grid { display: grid; grid-template-columns: 1fr 320px; }
              .pay { padding: 22px 26px 26px; min-width: 0; }
              h1 { margin: 0 0 12px; font-size: 26px; font-weight: 400; }
              .back { display: block; margin-bottom: 10px; border: 0; background: none; padding: 0; color: var(--teal);
                      font-size: 13px; cursor: pointer; }
              .back:hover:not(:disabled) { text-decoration: underline; }
              .back:disabled { color: var(--muted); cursor: default; }
              .methods { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; margin-bottom: 14px; }
              .method { padding: 12px 14px; border: 2px solid var(--line); border-radius: 10px; background: #fff; text-align: left;
                        font-size: 15px; font-weight: 600; cursor: pointer; }
              .method small { display: block; font-weight: 400; color: var(--muted); font-size: 12px; }
              .method[aria-pressed=true] { border-color: var(--teal); background: #f0faf9; }
              .method:disabled { opacity: .55; cursor: default; }
              .chips { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-bottom: 10px; font-size: 13px; color: var(--muted); }
              .chip { border: 1px solid var(--teal); color: var(--teal); background: #fff; border-radius: 16px; padding: 4px 12px;
                      font-size: 13px; cursor: pointer; }
              .chip:disabled { opacity: .5; cursor: default; }
              .primary { width: 100%; padding: 11px; border: 0; border-radius: 20px; background: var(--teal); color: #fff;
                         font-size: 15px; font-weight: 600; cursor: pointer; }
              .primary:disabled { background: #9fbfbc; cursor: default; }
              .status { min-height: 22px; margin: 6px 0; color: var(--teal-dark); font-weight: 500; }
              #frameTitle { padding: 8px 12px; border: 1px solid var(--line); border-bottom: 0; border-radius: 8px 8px 0 0;
                            background: #f6f8fa; font-size: 13px; color: var(--muted); }
              #frame { display: block; width: 100%; height: 1180px; border: 1px solid var(--line); border-radius: 0 0 8px 8px; background: #fff; }
              .summary { background: var(--summary); padding: 22px 26px; }
              .summary header { margin-bottom: 14px; font-size: 14px; }
              .sline { display: grid; grid-template-columns: 64px 1fr; gap: 14px; margin-bottom: 16px; }
              .tile { width: 64px; height: 64px; display: grid; place-items: center; border-radius: 8px; background: #fff; font-size: 34px; }
              .sline small { color: var(--muted); font-size: 11px; }
              .totals { border-top: 1px solid #d7dde1; margin-top: 18px; padding-top: 16px; font-size: 14px; }
              .totals div { display: flex; justify-content: space-between; margin-bottom: 4px; }
              .totals .total { border-top: 1px solid #d7dde1; margin-top: 18px; padding-top: 16px; }
              .totals .total b { font-size: 17px; }

              .console { max-width: 1180px; margin: 16px auto 32px; border: 1px solid #30363d; border-radius: 10px; overflow: hidden;
                         background: #0d1117; color: #c9d1d9; }
              .console header { display: flex; align-items: center; gap: 10px; padding: 8px 14px; background: #161b22;
                                border-bottom: 1px solid #30363d; font-size: 13px; color: #8b949e; }
              .console header b { color: #c9d1d9; font-weight: 600; }
              .console header button { margin-left: auto; border: 1px solid #30363d; border-radius: 6px; background: #21262d; color: #c9d1d9;
                                       padding: 3px 12px; font-size: 12px; cursor: pointer; }
              #log { height: 320px; overflow: auto; padding: 10px 14px; font: 12.5px/1.5 "Cascadia Mono", Consolas, monospace;
                     white-space: pre-wrap; word-break: break-all; }
              #log .empty { color: #6e7681; }
              #log .req { color: #56d4dd; } #log .step { color: #fff; font-weight: 600; margin-top: 8px; }
              #log .tag { color: #bc8cff; } #log .dim { color: #6e7681; } #log .warn { color: #d29922; }
              #log .err { color: #f85149; } #log .good { color: #3fb950; } #log .ok { color: #3fb950; font-weight: 700; }

              .overlay { position: fixed; inset: 0; display: grid; place-items: center; padding: 16px; background: rgba(0,0,0,.45); }
              .modal { width: min(520px, 100%); max-height: calc(100vh - 32px); overflow: auto; background: #fff; border-radius: 12px;
                       padding: 26px; box-shadow: 0 20px 60px rgba(0,0,0,.35); text-align: center; }
              .icon { width: 56px; height: 56px; margin: 0 auto 10px; display: grid; place-items: center; border-radius: 50%;
                      color: #fff; font-size: 30px; font-weight: 700; }
              .modal.approved .icon { background: var(--ok); } .modal.approved h2 { color: var(--ok); }
              .modal.declined .icon, .modal.error .icon { background: var(--bad); } .modal.declined h2, .modal.error h2 { color: var(--bad); }
              .modal h2 { margin: 0 0 6px; font-size: 22px; }
              .modal p { margin: 0 0 14px; color: #444; }
              .modal dl { display: grid; grid-template-columns: auto 1fr; gap: 4px 14px; margin: 0 0 14px; text-align: left; font-size: 14px; }
              .modal dt { color: var(--muted); } .modal dd { margin: 0; word-break: break-all; }
              .modal pre { margin: 0 0 14px; padding: 10px 12px; max-height: 220px; overflow: auto; text-align: left; border-radius: 6px;
                           background: #fff5f5; border: 1px solid #ffc9c9; color: #8a1c1c; font: 12px/1.45 "Cascadia Mono", Consolas, monospace;
                           white-space: pre-wrap; word-break: break-all; }
              .modal .actions { display: flex; gap: 10px; }
              .modal .actions button { flex: 1; padding: 10px; border-radius: 20px; font-size: 14px; font-weight: 600; cursor: pointer; }
              .modal .retry { border: 0; background: var(--teal); color: #fff; }
              .modal .tocart { border: 1px solid var(--teal); background: #fff; color: var(--teal); }
              @media (max-width: 1000px) { .grid { grid-template-columns: 1fr; } .summary { order: -1; } }
            </style>
            </head>
            <body>
              <div class="topbar"><div class="inner">💐 <b id="storeTop"></b> Checkout</div></div>
              <div class="shell">
                <div class="grid">
                  <section class="pay">
                    <button type="button" class="back" id="back">← Back to cart</button>
                    <h1>Payment</h1>
                    <div class="methods" id="methods" hidden>
                      <button type="button" class="method" id="methodC2p">Click to Pay<small>Mastercard Unified Checkout</small></button>
                      <button type="button" class="method" id="methodCard">Credit / Debit card<small id="methodCardSub"></small></button>
                    </div>
                    <div class="chips" id="chips">Fill a test card:</div>
                    <div id="hppStart" hidden>
                      <p>The card is entered in the PowerTranz Hosted Payment Page, shown below after you continue.</p>
                      <button type="button" class="primary" id="payHpp">Continue to secure payment</button>
                    </div>
                    <div class="status" id="status"></div>
                    <div id="frameTitle"></div>
                    <iframe id="frame" title="Secure payment (PowerTranz)"></iframe>
                  </section>
                  <aside class="summary">
                    <header>Order summary</header>
                    <div id="summary"></div>
                    <div class="totals">
                      <div><span>Subtotal:</span><span id="subtotal"></span></div>
                      <div><span>Shipping:</span><span id="shipping"></span></div>
                      <div class="total"><span>Total:</span><b id="total"></b></div>
                    </div>
                  </aside>
                </div>
              </div>

              <section class="console" aria-label="PowerTranz API console">
                <header><b>PowerTranz API console</b><span>live calls from the merchant server</span><button type="button" id="clear">Clear</button></header>
                <div id="log" role="log"></div>
              </section>

              <div class="overlay" id="overlay" hidden>
                <div class="modal" id="modal" role="dialog" aria-modal="true" aria-labelledby="outcomeTitle">
                  <div class="icon" id="outcomeIcon"></div>
                  <h2 id="outcomeTitle"></h2>
                  <p id="outcomeMessage"></p>
                  <dl id="outcomeDetails"></dl>
                  <pre id="outcomeException" hidden></pre>
                  <div class="actions">
                    <button type="button" class="tocart" id="toCart">Back to cart</button>
                    <button type="button" class="retry" id="retry"></button>
                  </div>
                </div>
              </div>
            <script>
            (() => {
              const data = {{data}};
              const $ = id => document.getElementById(id);
              const money = n => data.symbol + n.toFixed(2);
              const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
              const act = payload => window[data.action](JSON.stringify(payload));
              const frame = $('frame');

              // ---- Order summary ----
              $('storeTop').textContent = data.storeName;
              document.title = data.storeName + ' — Checkout';
              for (const l of data.lines) {
                const line = el('div', 'sline');
                const info = el('div'); info.append(el('div', '', l.name), el('div', '', money(l.price)), el('small', '', 'Quantity: ' + l.qty));
                line.append(el('div', 'tile', l.emoji), info);
                $('summary').append(line);
              }
              $('subtotal').textContent = money(data.subtotal);
              $('shipping').textContent = money(data.shipping);
              $('total').textContent = money(data.total);

              function setBusy(busy, text) {
                // Back and the payment methods stay enabled: they cancel the attempt in progress.
                document.querySelectorAll('.chip, #payHpp').forEach(c => { c.disabled = busy; });
                $('status').textContent = text || '';
              }
              window.setBusy = setBusy;

              // ---- Checkout form (the HPP template, loaded unmodified) ----
              const formDoc = () => { try { return frame.contentDocument; } catch { return null; } };
              function showForm() {
                frame.removeAttribute('srcdoc');
                frame.src = data.formUrl + '?t=' + Date.now();
                $('frameTitle').textContent = 'Checkout form (PowerTranz HPP template)';
              }
              // Fills what PowerTranz fills when it serves the template: the amount and the currency code.
              frame.addEventListener('load', () => {
                const doc = formDoc();
                if (!doc || !doc.getElementById('FrmCheckout')) return;
                const amount = doc.getElementById('Amount');
                if (amount) amount.value = data.total.toFixed(2);
                const walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT);
                while (walker.nextNode())
                  if (walker.currentNode.nodeValue.includes('{PTZ_CURRENCY_CODE}'))
                    walker.currentNode.nodeValue = walker.currentNode.nodeValue.replace('{PTZ_CURRENCY_CODE}', data.currencyCode);
              });
              function fillTestCard(c) {
                const doc = formDoc();
                if (!doc || !doc.getElementById('FrmCheckout')) return;
                const set = (id, v) => {
                  const input = doc.getElementById(id);
                  if (!input || (input.value && id.startsWith('BillTo'))) return;
                  input.value = v;
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                  input.dispatchEvent(new Event('change', { bubbles: true }));
                };
                set('CardholderName', 'John Doe');
                set('CardNo', c.pan);
                set('CardExpDate', c.expiration);
                set('CardCVV2', c.cvv);
                set('BillToEmail', 'john.doe@example.com');
              }

              // Hosted Payment Page: every attempt starts a new transaction and PowerTranz serves the form in the iframe.
              const startHpp = () => {
                frame.removeAttribute('srcdoc');
                frame.src = 'about:blank';
                $('frameTitle').textContent = 'PowerTranz Hosted Payment Page';
                setBusy(true, 'Requesting the secure payment form from PowerTranz…');
                act({ action: 'pay' });
              };
              if (data.hostedPage) {
                $('chips').textContent = 'Staging test card: 4012 0000 0002 0006 · Exp 1226 · CVV 123 · 3DS password 3ds2';
                $('payHpp').onclick = startHpp;
              } else {
                for (const c of data.testCards) {
                  const b = el('button', 'chip', c.label);
                  b.type = 'button';
                  b.onclick = () => fillTestCard(c);
                  $('chips').append(b);
                }
              }

              // ---- Payment method: Click to Pay (Unified Checkout in the iframe) or card (HPP / checkout form) ----
              let method = 'card';
              function showClickToPay() {
                frame.removeAttribute('srcdoc');
                frame.src = data.clickToPayUrl + '?t=' + Date.now();
                $('frameTitle').textContent = 'Mastercard Click to Pay (Unified Checkout)';
                $('status').textContent = '';
              }
              const startCard = () => data.hostedPage ? startHpp() : showForm();
              function selectMethod(m, userClick) {
                // Leaving a method mid-attempt (e.g. the HPP or 3DS page is open) cancels it on the server.
                if (userClick) { act({ action: 'cancel' }); setBusy(false); }
                method = m;
                $('methodC2p').setAttribute('aria-pressed', String(m === 'c2p'));
                $('methodCard').setAttribute('aria-pressed', String(m === 'card'));
                $('chips').hidden = m === 'c2p';
                if (m === 'c2p') showClickToPay(); else startCard();
              }
              $('methodCardSub').textContent = data.hostedPage ? 'PowerTranz Hosted Payment Page' : 'PowerTranz checkout form';
              if (data.clickToPay) {
                $('methods').hidden = false;
                $('methodC2p').onclick = () => selectMethod('c2p', true);
                $('methodCard').onclick = () => selectMethod('card', true);
                selectMethod('c2p');
              } else {
                selectMethod('card');
              }
              $('back').onclick = () => {
                $('back').disabled = true;
                frame.removeAttribute('srcdoc');
                frame.src = 'about:blank';
                setBusy(true, 'Returning to cart…');
                act({ action: 'back' });
              };

              // Called by the server with the Sale RedirectData: 3DS (template mode) or the Hosted Payment Page.
              window.showRedirect = html => {
                const hpp = data.hostedPage && method === 'card';
                $('frameTitle').textContent = hpp
                  ? 'PowerTranz Hosted Payment Page — enter the card and complete any verification here.'
                  : '3-D Secure — if your bank asks for verification, complete it here (staging test password: 3ds2).';
                frame.srcdoc = html;
                $('status').textContent = hpp ? 'Enter the card in the secure form below.' : 'Authenticating the cardholder (3-D Secure)…';
              };

              // Called by the server when the attempt ends.
              window.showOutcome = o => {
                setBusy(false);
                if (method === 'c2p' || data.hostedPage) { frame.removeAttribute('srcdoc'); frame.src = 'about:blank'; } else showForm();
                $('modal').className = 'modal ' + o.kind;
                $('outcomeIcon').textContent = o.kind === 'approved' ? '✓' : '✕';
                $('outcomeTitle').textContent = o.title;
                $('outcomeMessage').textContent = o.message;
                const dl = $('outcomeDetails'); dl.replaceChildren();
                for (const d of o.details) dl.append(el('dt', '', d.label), el('dd', '', d.value));
                $('outcomeException').textContent = o.exception || '';
                $('outcomeException').hidden = !o.exception;
                $('retry').textContent = o.kind === 'approved' ? 'New payment'
                  : (method === 'c2p' || data.hostedPage ? 'Try again' : 'Try another card');
                $('overlay').hidden = false;
                $('retry').focus();
              };
              $('retry').onclick = () => {
                $('overlay').hidden = true;
                if (method === 'c2p') showClickToPay(); else if (data.hostedPage) startHpp();
              };
              $('toCart').onclick = () => { $('overlay').hidden = true; $('back').onclick(); };

              // ---- Live API console ----
              const log = $('log');
              const showEmpty = () => log.replaceChildren(el('div', 'empty', 'Waiting for PowerTranz calls…'));
              function lineClass(l) {
                const t = l.t;
                if (l.e) return 'err';
                if (/^(POST|GET) /.test(t)) return 'req';
                if (/^STEP /.test(t)) return 'step';
                if (/^RESULT: PAYMENT APPROVED/.test(t)) return 'ok';
                if (/^RESULT: /.test(t)) return 'err';
                if (/^\s+=> .*(NOT|ERROR)/.test(t) || /^\[warn\]/.test(t)) return 'warn';
                if (/^\s+=> /.test(t)) return 'good';
                if (/^\[(3DS|PowerTranz|browser|HPP form)\]/.test(t)) return 'tag';
                if (/^(Current URL|\s+\[iframe\]|\s+\(|Callback fields|\s+Headers:)/.test(t)) return 'dim';
                return '';
              }
              let next = data.logStart, pulling = false, empty = true;
              showEmpty();
              setInterval(async () => {
                if (pulling) return;
                pulling = true;
                try {
                  const r = JSON.parse(await window[data.pull](next));
                  next = r.next;
                  if (r.lines.length && empty) { log.replaceChildren(); empty = false; }
                  const stick = log.scrollTop + log.clientHeight >= log.scrollHeight - 20;
                  for (const l of r.lines) log.append(el('div', lineClass(l), l.t || ' '));
                  if (stick) log.scrollTop = log.scrollHeight;
                } catch { /* page is being replaced */ }
                finally { pulling = false; }
              }, 400);
              $('clear').onclick = () => { showEmpty(); empty = true; };
            })();
            </script>
            </body>
            </html>
            """;
    }
}
