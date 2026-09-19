/// Minimal HS256 JWT signing for PowerSync. Hand-rolled to keep the
/// dependency list short; the token is three base64url segments.
module Server.Jwt

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

let private base64Url (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private encode (s: string) = base64Url (Encoding.UTF8.GetBytes s)

let create (secret: string) (kid: string) (audience: string) (subject: string) (lifetime: TimeSpan) =
    let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

    let header = JsonSerializer.Serialize {| alg = "HS256"; typ = "JWT"; kid = kid |}

    let payload =
        JsonSerializer.Serialize
            {| sub = subject
               aud = audience
               iat = now
               exp = now + int64 lifetime.TotalSeconds |}

    let signingInput = encode header + "." + encode payload
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
    signingInput + "." + base64Url (hmac.ComputeHash(Encoding.UTF8.GetBytes signingInput))
