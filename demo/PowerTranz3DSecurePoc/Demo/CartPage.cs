using System.Text.Json;
using PowerTranz3DSecurePoc.Shop;

namespace PowerTranz3DSecurePoc.Demo;

/// <summary>
/// Minimal shop cart + order summary. "Check out" sends only product ids and quantities through the
/// Playwright-exposed function <see cref="CheckoutFunctionName"/>; the order is priced server-side.
/// </summary>
public static class CartPage
{
    public const string CheckoutFunctionName = "__checkout";

    /// <param name="quantities">Cart to restore (product id → quantity); null uses each product's InitialQuantity.</param>
    public static string Render(ShopOptions shop, IReadOnlyDictionary<string, int>? quantities = null)
    {
        // The default System.Text.Json encoder escapes <, > and &, so this is safe inside <script>.
        var data = JsonSerializer.Serialize(new
        {
            storeName = shop.StoreName,
            symbol = shop.CurrencySymbol,
            shipping = shop.ShippingCost,
            maxQty = OrderPricing.MaxQuantityPerLine,
            submit = CheckoutFunctionName,
            products = shop.Products.Select(p => new
            {
                id = p.Id, name = p.Name, description = p.Description, price = p.Price, emoji = p.Emoji,
                qty = quantities is null ? p.InitialQuantity : quantities.GetValueOrDefault(p.Id),
            }),
        });

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>Cart</title>
            <style>
              :root { --ink: #1f1f1f; --muted: #6b6b6b; --line: #e3e3e3; --teal: #0f7f78; --teal-dark: #0b645f;
                      --summary: #f3f6f8; --page: #f2f2f2; --tile: #fdf1f4; }
              * { box-sizing: border-box; }
              button { font-family: inherit; }
              body { margin: 0; background: var(--page); color: var(--ink); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
              .topbar { background: #141414; color: #fff; padding: 18px 16px; }
              .topbar .inner { max-width: 1100px; margin: 0 auto; display: flex; align-items: center; gap: 10px; font-size: 22px; }
              .topbar b { font-weight: 700; }
              .shell { max-width: 1100px; margin: 0 auto 40px; background: #fff; box-shadow: 0 1px 3px rgba(0,0,0,.06); }
              .nav { display: grid; grid-template-columns: 40px 1fr 40px; align-items: center; padding: 14px 24px;
                     border-bottom: 1px solid var(--line); }
              .burger { width: 20px; height: 14px; border-top: 2px solid #444; border-bottom: 2px solid #444; position: relative; }
              .burger::after { content: ""; position: absolute; left: 0; right: 0; top: 4px; border-top: 2px solid #444; }
              .brand { text-align: center; font-weight: 600; font-size: 16px; }
              .brand span { display: inline-grid; place-items: center; width: 22px; height: 22px; margin-right: 6px;
                            border-radius: 4px; background: var(--teal); color: #fff; font-size: 13px; vertical-align: -4px; }
              .cart-icon { position: relative; justify-self: end; font-size: 20px; }
              .badge { position: absolute; top: -6px; right: -8px; min-width: 16px; height: 16px; padding: 0 4px;
                       border-radius: 8px; background: var(--teal); color: #fff; font-size: 11px; line-height: 16px; text-align: center; }
              .grid { display: grid; grid-template-columns: 1fr 370px; }
              .cart { padding: 22px 26px 32px; }
              h1 { margin: 0 0 12px; font-size: 26px; font-weight: 400; }
              h1 small { font-size: 13px; color: var(--muted); margin-left: 6px; }
              .item { display: grid; grid-template-columns: 90px 1fr auto; gap: 16px; align-items: center;
                      padding: 16px 0; border-bottom: 1px solid var(--line); }
              .tile { width: 90px; height: 90px; display: grid; place-items: center; border-radius: 8px;
                      background: var(--tile); font-size: 48px; }
              .item .name { font-weight: 500; }
              .item .desc { color: var(--muted); font-size: 13px; }
              .remove { border: 0; background: none; padding: 0; margin-top: 4px; color: var(--ink); font-size: 12px; cursor: pointer; }
              .remove:hover { text-decoration: underline; }
              .stepper { display: inline-flex; border: 1px solid #c9c9c9; border-radius: 4px; overflow: hidden; }
              .stepper button { width: 30px; height: 32px; border: 0; background: #fff; font-size: 16px; cursor: pointer; }
              .stepper button:disabled { color: #bbb; cursor: default; }
              .stepper span { width: 30px; display: grid; place-items: center; border-left: 1px solid #c9c9c9;
                              border-right: 1px solid #c9c9c9; font-size: 13px; }
              .checkout { margin-top: 28px; width: 256px; padding: 10px; border: 0; border-radius: 20px; background: var(--teal);
                          color: #fff; font-size: 15px; font-weight: 600; cursor: pointer; }
              .checkout:hover:not(:disabled) { background: var(--teal-dark); }
              .checkout:disabled { background: #9fbfbc; cursor: default; }
              .empty { padding: 28px 0; color: var(--muted); }
              .more { margin-top: 34px; }
              .more h2 { font-size: 15px; font-weight: 600; margin: 0 0 8px; }
              .more .item { grid-template-columns: 56px 1fr auto; padding: 10px 0; }
              .more .tile { width: 56px; height: 56px; font-size: 30px; }
              .add { border: 1px solid var(--teal); color: var(--teal); background: #fff; border-radius: 16px;
                     padding: 5px 14px; cursor: pointer; font-weight: 600; }
              .summary { background: var(--summary); padding: 22px 26px; }
              .summary header { display: flex; justify-content: space-between; margin-bottom: 14px; font-size: 14px; }
              .summary header a { color: var(--teal); text-decoration: none; font-size: 13px; }
              .sline { display: grid; grid-template-columns: 70px 1fr; gap: 14px; margin-bottom: 16px; }
              .sline .tile { width: 70px; height: 70px; font-size: 36px; background: #fff; }
              .sline small { color: var(--muted); font-size: 11px; }
              .totals { border-top: 1px solid #d7dde1; margin-top: 18px; padding-top: 16px; font-size: 14px; }
              .totals div { display: flex; justify-content: space-between; margin-bottom: 4px; }
              .totals .total { border-top: 1px solid #d7dde1; margin-top: 18px; padding-top: 16px; }
              .totals .total b { font-size: 17px; }
              #overlay { display: none; position: fixed; inset: 0; background: rgba(255,255,255,.85);
                         place-items: center; font-size: 18px; }
              @media (max-width: 820px) { .grid { grid-template-columns: 1fr; } }
            </style>
            </head>
            <body>
              <div class="topbar"><div class="inner">💐 <b id="storeTop"></b> Checkout</div></div>
              <div class="shell">
                <div class="nav">
                  <div class="burger" aria-hidden="true"></div>
                  <div class="brand"><span>✿</span><b id="storeNav"></b></div>
                  <div class="cart-icon" aria-label="Cart">🛒<span class="badge" id="badge"></span></div>
                </div>
                <div class="grid">
                  <section class="cart">
                    <h1>Cart <small id="count"></small></h1>
                    <div id="items"></div>
                    <button class="checkout" id="checkout">Check out</button>
                    <div class="more" id="moreBox"><h2>Add more bouquets</h2><div id="more"></div></div>
                  </section>
                  <aside class="summary">
                    <header><span>Order summary</span><a href="#items" id="edit">Edit cart</a></header>
                    <div id="summary"></div>
                    <div class="totals">
                      <div><span>Subtotal:</span><span id="subtotal"></span></div>
                      <div><span>Shipping:</span><span id="shipping"></span></div>
                      <div class="total"><span>Total:</span><b id="total"></b></div>
                    </div>
                  </aside>
                </div>
              </div>
              <div id="overlay">Redirecting to secure payment…</div>
            <script>
            (() => {
              const data = {{data}};
              const $ = id => document.getElementById(id);
              const money = n => data.symbol + n.toFixed(2);
              const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
              const products = data.products.map(p => ({ ...p }));

              $('storeTop').textContent = data.storeName;
              $('storeNav').textContent = data.storeName;
              document.title = data.storeName + ' — Cart';

              function stepper(p) {
                const box = el('div', 'stepper');
                const minus = el('button', '', '−'); minus.setAttribute('aria-label', 'Decrease quantity');
                const plus = el('button', '', '+'); plus.setAttribute('aria-label', 'Increase quantity');
                plus.disabled = p.qty >= data.maxQty;
                minus.onclick = () => { p.qty = Math.max(0, p.qty - 1); render(); };
                plus.onclick = () => { p.qty = Math.min(data.maxQty, p.qty + 1); render(); };
                box.append(minus, el('span', '', String(p.qty)), plus);
                return box;
              }

              function render() {
                const inCart = products.filter(p => p.qty > 0);
                const count = inCart.reduce((n, p) => n + p.qty, 0);
                const subtotal = inCart.reduce((s, p) => s + p.price * p.qty, 0);
                const shipping = inCart.length ? data.shipping : 0;

                $('badge').textContent = count;
                $('count').textContent = '(' + count + (count === 1 ? ' Item)' : ' Items)');

                const items = $('items'); items.replaceChildren();
                if (!inCart.length) items.append(el('div', 'empty', 'Your cart is empty.'));
                for (const p of inCart) {
                  const row = el('div', 'item');
                  const info = el('div');
                  const remove = el('button', 'remove', '✕  Remove');
                  remove.onclick = () => { p.qty = 0; render(); };
                  info.append(el('div', 'name', p.name), el('div', 'desc', p.description), el('div', '', money(p.price)), remove);
                  row.append(el('div', 'tile', p.emoji), info, stepper(p));
                  items.append(row);
                }

                const more = $('more'); more.replaceChildren();
                const notInCart = products.filter(p => p.qty === 0);
                $('moreBox').style.display = notInCart.length ? '' : 'none';
                for (const p of notInCart) {
                  const row = el('div', 'item');
                  const info = el('div'); info.append(el('div', 'name', p.name), el('div', 'desc', money(p.price)));
                  const add = el('button', 'add', 'Add'); add.onclick = () => { p.qty = 1; render(); };
                  row.append(el('div', 'tile', p.emoji), info, add);
                  more.append(row);
                }

                const summary = $('summary'); summary.replaceChildren();
                for (const p of inCart) {
                  const line = el('div', 'sline');
                  const info = el('div'); info.append(el('div', '', p.name), el('div', '', money(p.price)), el('small', '', 'Quantity: ' + p.qty));
                  line.append(el('div', 'tile', p.emoji), info);
                  summary.append(line);
                }

                $('subtotal').textContent = money(subtotal);
                $('shipping').textContent = money(shipping);
                $('total').textContent = money(subtotal + shipping);
                $('checkout').disabled = !inCart.length;
              }

              $('checkout').onclick = () => {
                const items = products.filter(p => p.qty > 0).map(p => ({ id: p.id, quantity: p.qty }));
                if (!items.length) return;
                $('checkout').disabled = true;
                $('overlay').style.display = 'grid';
                window[data.submit](JSON.stringify(items));
              };

              render();
            })();
            </script>
            </body>
            </html>
            """;
    }
}
