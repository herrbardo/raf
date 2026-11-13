using System.Net;
using System.Net.Sockets;

namespace Raf.Server.Relay;

/// <summary>
/// Qué direcciones puede alcanzar el relay. Se controla la IP ya resuelta, no el nombre: un dominio público que
/// apunta a 127.0.0.1 o a la red interna se bloquea igual (SSRF).
/// </summary>
public static class RelayAddressPolicy
{
    /// <summary>
    /// False para loopback, redes privadas, link-local (incluida la metadata de las nubes, 169.254.169.254),
    /// CGNAT, multicast, reservadas, IPv6 de sitio local (ULA, la red privada de Railway) y las IPv4 embebidas en IPv6.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 or >= 224
                     || (b[0] == 100 && (b[1] & 0xC0) == 64)      // 100.64.0.0/10 (CGNAT)
                     || (b[0] == 169 && b[1] == 254)              // link-local y metadata
                     || (b[0] == 172 && (b[1] & 0xF0) == 16)      // 172.16.0.0/12
                     || (b[0] == 192 && b[1] == 168)              // 192.168.0.0/16
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)   // 192.0.0.0/24
                     || (b[0] == 198 && (b[1] & 0xFE) == 18));    // 198.18.0.0/15

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0) return false;         // ::, ::1 e IPv4 compatibles
        if ((b[0] & 0xFE) == 0xFC) return false;                                 // fc00::/7 (ULA)
        if (b[0] == 0xFE && (b[1] & 0x80) == 0x80) return false;                 // fe80::/10 y fec0::/10
        if (b[0] == 0xFF) return false;                                          // multicast
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)        // 64:ff9b::/96 (NAT64)
            return IsPublic(new IPAddress(b.AsSpan(12, 4)));
        if (b[0] == 0x20 && b[1] == 0x02)                                        // 2002::/16 (6to4)
            return IsPublic(new IPAddress(b.AsSpan(2, 4)));
        return true;
    }
}
