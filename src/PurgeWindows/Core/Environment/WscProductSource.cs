using System.Globalization;
using System.Reflection;

namespace PurgeWindows.Core.Environment;

/// <summary>
/// Windows Security Center enumeration via the scriptable <c>WSCAPI.WSCProductList.1</c> COM
/// object (the primary antivirus source, W5 §5.2 / W4 E12). Enumeration only: the app never
/// registers anything with Security Center. Any failure returns a failed result so the caller
/// falls back to WMI — an empty successful enumeration means "none registered", never failure.
/// </summary>
public static class WscProductSource
{
    // WSC_SECURITY_PROVIDER (wscapi.h): FIREWALL=0x1, AUTOUPDATE=0x2, ANTIVIRUS=0x4, ANTISPYWARE=0x8.
    public const int ProviderFirewall = 0x1;
    public const int ProviderAntivirus = 0x4;
    public const int ProviderAntiSpyware = 0x8;

    private const string ProgId = "WSCAPI.WSCProductList.1";

    /// <summary>Enumerates the registered products for one provider. Late-bound COM, read-only.</summary>
    public static WscQueryResult Query(int provider)
    {
        object? productList = null;
        try
        {
            var type = Type.GetTypeFromProgID(ProgId);
            if (type is null) return WscQueryResult.Failed();
            productList = Activator.CreateInstance(type);
            if (productList is null) return WscQueryResult.Failed();

            // IWSCProductList::Initialize(provider) — enumerates the registered products.
            type.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, productList,
                [provider], CultureInfo.InvariantCulture);
            var countValue = type.InvokeMember("Count", BindingFlags.GetProperty, null, productList,
                null, CultureInfo.InvariantCulture);
            // Count is ULONG: late binding may surface it as uint, int, or long.
            var count = AsInt(countValue, 0);

            var products = new List<WscProductRow>();
            for (var i = 0; i < count; i++)
            {
                object? product = null;
                try
                {
                    // IWSCProductList::Item is a parameterized property: Item[i].
                    product = type.InvokeMember("Item", BindingFlags.GetProperty, null, productList,
                        [i], CultureInfo.InvariantCulture);
                    if (product is null) continue;
                    var productType = product.GetType();
                    var name = productType.InvokeMember("ProductName", BindingFlags.GetProperty, null, product,
                        null, CultureInfo.InvariantCulture) as string;
                    var stateValue = productType.InvokeMember("ProductState", BindingFlags.GetProperty, null, product,
                        null, CultureInfo.InvariantCulture);
                    var signatureValue = productType.InvokeMember("SignatureStatus", BindingFlags.GetProperty, null, product,
                        null, CultureInfo.InvariantCulture);
                    products.Add(new WscProductRow(
                        name ?? "",
                        AsInt(stateValue, -1),
                        AsInt(signatureValue, -1)));
                }
                catch
                {
                    // One unreadable product must not fail the whole enumeration.
                    continue;
                }
                finally
                {
                    ComHelper.Release(product);
                }
            }
            return WscQueryResult.Ok(products);
        }
        catch
        {
            return WscQueryResult.Failed();
        }
        finally
        {
            ComHelper.Release(productList);
        }
    }

    /// <summary>Coerces a late-bound COM number (int, uint, long, short, byte) to int.</summary>
    static int AsInt(object? value, int fallback) => value switch
    {
        int i => i,
        uint u => (int)u,
        long l => (int)l,
        short s => s,
        byte b => b,
        _ => fallback,
    };
}
