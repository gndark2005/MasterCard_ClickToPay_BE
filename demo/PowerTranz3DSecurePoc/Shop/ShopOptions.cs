namespace PowerTranz3DSecurePoc.Shop;

/// <summary>Bound from the "Shop" configuration section.</summary>
public sealed class ShopOptions
{
    public const string SectionName = "Shop";

    public string StoreName { get; set; } = "Bloom Bouquets";
    public string CurrencySymbol { get; set; } = "€";
    public decimal ShippingCost { get; set; }
    public List<Product> Products { get; set; } = [];
}

public sealed class Product
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string Emoji { get; set; } = "💐";
    /// <summary>Quantity pre-loaded in the cart when the demo starts (0 = shown under "Add more bouquets").</summary>
    public int InitialQuantity { get; set; }
}

public sealed record OrderLine(Product Product, int Quantity)
{
    public decimal LineTotal => Product.Price * Quantity;
}

public sealed record Order(IReadOnlyList<OrderLine> Lines, decimal Shipping)
{
    public decimal Subtotal => Lines.Sum(l => l.LineTotal);
    public decimal Total => Subtotal + Shipping;
}

/// <summary>What the cart page sends: product ids and quantities only. Prices are never taken from the browser.</summary>
public sealed record CartItemInput(string? Id, int Quantity);

public static class OrderPricing
{
    public const int MaxQuantityPerLine = 20;

    /// <summary>Prices the cart from the server-side catalog; unknown ids and invalid quantities are rejected.</summary>
    public static Order Price(IEnumerable<CartItemInput> items, ShopOptions shop)
    {
        var lines = new List<OrderLine>();
        foreach (var group in items.Where(i => i.Quantity > 0).GroupBy(i => i.Id))
        {
            var product = shop.Products.FirstOrDefault(p => p.Id == group.Key)
                          ?? throw new InvalidOperationException($"Unknown product id '{group.Key}'.");
            var quantity = group.Sum(i => i.Quantity);
            if (quantity > MaxQuantityPerLine)
                throw new InvalidOperationException($"Quantity for '{product.Name}' exceeds {MaxQuantityPerLine}.");
            lines.Add(new OrderLine(product, quantity));
        }

        if (lines.Count == 0)
            throw new InvalidOperationException("The cart is empty.");

        return new Order(lines, shop.ShippingCost);
    }
}
