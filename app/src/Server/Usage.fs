/// Usage counters, written to a QuestDB if one is configured.
///
/// The store is a QuestDB, wherever `O11Y_ENDPOINT` points: one POST of
/// InfluxDB line protocol to its `/write`, with the write key in an
/// `X-API-Key` header. Nothing here is specific to a particular host or a
/// particular dashboard, and without an endpoint and a key - which a fresh
/// deployment has neither of - nothing is written at all. Three tables, which
/// ILP creates on first write:
///
///     ptp_pageview   env, section, value, timestamp
///     ptp_login      env, provider, value, timestamp
///     ptp_write      env, source, resource, action, value, timestamp
///
/// Counts, not people: no user id, no session, no address ever goes into a
/// row, and nothing here is read back by the app. Two visits by one person are
/// two visits, which is what was asked for.
///
/// Fire-and-forget, always. A counter is never worth failing a request over
/// and never worth delaying one, so every function here returns immediately
/// and the write settles on its own. A failure is logged once and dropped:
/// retrying page views is how somebody else's outage becomes memory pressure
/// here. With no endpoint or no key - local dev, CI - nothing is sent at all.
module Server.Usage

open System
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Server.Config

let private http = new HttpClient(Timeout = TimeSpan.FromSeconds 3.)

let enabled (config: Config) =
    config.O11yEndpoint <> "" && config.O11yApiKey <> ""

/// Line-protocol escaping for a tag value.
///
/// A space, comma or equals sign ends a tag in ILP, so an unescaped one does
/// not corrupt the row - it quietly writes a *different* row, with a field
/// boundary where the value should have been. Everything passed here comes
/// from a fixed list and would survive without this; it is here so the next
/// tag added cannot be the one that does not.
let private tag (value: string) =
    value.Replace("\\", "\\\\").Replace(" ", "\\ ").Replace(",", "\\,").Replace("=", "\\=")

let private send (config: Config) (lines: string list) =
    if enabled config && not lines.IsEmpty then
        // QuestDB reads ILP timestamps as nanoseconds; the three zeros are the
        // unit conversion from milliseconds, not precision we have.
        let at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L
        let body = lines |> List.map (fun line -> $"{line} {at}") |> String.concat "\n"

        let write () : Task =
            task {
                try
                    use request = new HttpRequestMessage(HttpMethod.Post, config.O11yEndpoint + "/write")
                    request.Headers.TryAddWithoutValidation("X-API-Key", config.O11yApiKey) |> ignore
                    request.Content <- new StringContent(body, Encoding.UTF8, "text/plain")
                    use! response = http.SendAsync request

                    if not response.IsSuccessStatusCode then
                        eprintfn "usage: %d writing %s" (int response.StatusCode) (String.concat "; " lines)
                with e ->
                    eprintfn "usage: write failed: %s" e.Message
            }

        write () |> ignore

/// One page opened, named by `Shared.Usage.sections`.
let pageview (config: Config) (section: string) =
    send config [ $"ptp_pageview,env={tag config.O11yEnv},section={tag section} value=1i" ]

/// One completed sign-in, counted where the session is actually created.
///
/// Not from the browser, deliberately: a page view is "this was opened", which
/// only the browser knows, but a login is "a session began", which only the
/// callback that began it knows. Ask the browser and every restored session and
/// second tab is a login. Cookie renewals are not sign-ins either, and do not
/// come through here - see Auth.validateSession.
let login (config: Config) (provider: string) =
    send config [ $"ptp_login,env={tag config.O11yEnv},provider={tag provider} value=1i" ]

/// Rows changed, batched: one line per (resource, action) with how many of
/// them there were, so a recipe's worth of shopping items is one line rather
/// than a dozen writes. `source` is what did the changing - the app, or an
/// assistant through the MCP server.
let changes (config: Config) (source: string) (counts: (string * string * int) list) =
    send
        config
        [ for resource, action, count in counts ->
              $"ptp_write,env={tag config.O11yEnv},source={tag source},resource={tag resource},action={tag action} "
              + $"value={count}i" ]

/// One MCP tool call that changed something, against the area it changed.
let mcp (config: Config) (resource: string) = changes config "mcp" [ resource, "mutate", 1 ]
