using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;

namespace AgentRuntime.Tui;

/// <summary>
/// **WB Web UI（本地版）**：浏览器负责呈现与输入，运行时/引擎完全复用。
/// <para>只监听 127.0.0.1；`GET /` 页面，`GET /state` 读事件流，`POST /send` 入队。
/// 终端 `--web` 只做启动器与日志。</para>
/// </summary>
internal static class WebUi
{
    public static async Task<int> RunAsync(
        RuntimeHost host,
        string configPath,
        TuiOptions options,
        ContinuationSettings continuation,
        ContinuationBudget budget)
    {
        var ledger = new TurnLedger();
        var ablation = new AblationService();
        var router = new PanelRouter(host, ledger, ablation);
        var inbox = new ConcurrentQueue<string>();
        var busy = false;
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var port = PickPort(5280);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine($"[web] WB Web UI: http://127.0.0.1:{port}/  （Ctrl-C 退出）");

        _ = Task.Run(() => ServeAsync(listener, inbox, host, cts.Token));
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (!inbox.TryDequeue(out var text))
                {
                    await Task.Delay(120, cts.Token).ConfigureAwait(false);
                    continue;
                }

                busy = true;
                try
                {
                    if (PanelRouter.IsCommand(text))
                    {
                        await router.ExecuteAsync(text, Console.Error).ConfigureAwait(false);
                    }
                    else
                    {
                        var cursor = host.StreamCount;
                        await router.RunTurnAsync(PanelRouter.Unescape(text), options.Verbose).ConfigureAwait(false);
                        budget.Reset();   // 每条链各自一份预算
                        await TurnContinuation.RunAsync(
                            host,
                            cursor,
                            continuation,
                            budget,
                            step: token => router.ContinueTurnAsync(options.Verbose, token)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[web] 回合失败：{ex.Message}");
                }
                finally
                {
                    busy = false;
                }
            }
        }, cts.Token);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        return 0;
    }

    private static async Task ServeAsync(HttpListener listener, ConcurrentQueue<string> inbox, RuntimeHost host, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    var path = context.Request.Url?.AbsolutePath ?? "/";
                    if (path == "/send" && context.Request.HttpMethod == "POST")
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        var text = (await reader.ReadToEndAsync().ConfigureAwait(false)).Trim();
                        if (text.Length > 0) inbox.Enqueue(text);
                        context.Response.StatusCode = 200;
                        await context.Response.OutputStream.WriteAsync("ok"u8.ToArray()).ConfigureAwait(false);
                        context.Response.Close();
                        return;
                    }

                    if (path == "/state")
                    {
                        var body = State(host);
                        var bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        context.Response.Close();
                        return;
                    }

                    var html = Encoding.UTF8.GetBytes(Page());
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = html.Length;
                    await context.Response.OutputStream.WriteAsync(html).ConfigureAwait(false);
                    context.Response.Close();
                }
                catch
                {
                    try { context.Response.Abort(); } catch { }
                }
            }, ct);
        }
    }

    /// <summary>纯文本状态（避免把 JSON 引回运行时）：行 = <c>kind\u001ftext</c>；首行 busy/closed。</summary>
    private static string State(RuntimeHost host)
    {
        var stream = host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        var sb = new StringBuilder();
        sb.Append(host.SessionClosed ? "closed" : "open").Append('\n');
        if (stream is not null)
        {
            foreach (var e in stream.Events.TakeLast(300))
            {
                if (e.Kind is not (AgentRuntime.Core.Stream.SessionEventKind.AgentOutput
                    or AgentRuntime.Core.Stream.SessionEventKind.UserInput
                    or AgentRuntime.Core.Stream.SessionEventKind.ToolDenied
                    or AgentRuntime.Core.Stream.SessionEventKind.Hint))
                {
                    continue;
                }

                var text = e.Text.Replace('\u001f', ' ').Replace('\u001e', ' ').Replace('\n', '\u001d');
                sb.Append(e.Kind.ToString()).Append('\u001f').Append(text).Append('\u001e').Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string Page() => """
<!doctype html>
<html lang="zh-CN"><meta charset="utf-8">
<title>WB Web UI</title>
<style>
 :root{color-scheme:light dark}
 body{font:16px/1.6 -apple-system,BlinkMacSystemFont,"PingFang SC",sans-serif;margin:0;height:100vh;display:flex;flex-direction:column}
 header{padding:10px 16px;border-bottom:1px solid #8884;display:flex;gap:16px;align-items:center;font-size:14px}
 #log{flex:1;overflow:auto;padding:16px;white-space:pre-wrap}
 .u{color:#2f6fed;font-weight:600;margin-top:14px}
 .a{margin-top:4px}
 .h{opacity:.55;font-size:13px}
 .d{color:#d35}
 footer{padding:12px 16px;border-top:1px solid #8884;display:flex;gap:10px}
 textarea{flex:1;min-height:70px;max-height:40vh;resize:vertical;font:16px/1.5 "PingFang SC",ui-monospace,monospace;padding:10px;border:1px solid #8886;border-radius:8px}
 button{padding:10px 18px;font-size:16px;border-radius:8px;border:1px solid #8886;background:#2f6fed;color:#fff;cursor:pointer;align-self:flex-end}
</style>
<header><b>WB Web UI</b><span id=status>连接中…</span></header>
<div id=log></div>
<footer><textarea id=text autofocus placeholder="输入或粘贴（Cmd+Enter 发送）"></textarea><button id=send>发送</button></footer>
<script>
const log=document.getElementById('log'), st=document.getElementById('status'), ta=document.getElementById('text');
let last='';
async function tick(){
  try{
    const r=await fetch('/state',{cache:'no-store'}); const raw=await r.text();
    if(raw===last) return; last=raw;
    const lines=raw.split('\n'); st.textContent='会话 '+(lines[0]==='closed'?'已收尾（发 /start）':'运行中');
    const frag=document.createDocumentFragment();
    for(const line of lines.slice(1)){
      if(!line) continue;
      const i=line.indexOf('\u001f'); if(i<0) continue;
      const kind=line.slice(0,i), text=line.slice(i+1).replaceAll('\u001d','\n').replaceAll('\u001e','');
      if(kind==='Hint') continue;
      const div=document.createElement('div');
      div.className=kind==='UserInput'?'u':(kind==='ToolDenied'?'d':'a');
      div.textContent=(kind==='UserInput'?'你 > ':'')+text;
      frag.appendChild(div);
    }
    log.replaceChildren(frag); log.scrollTop=log.scrollHeight;
  }catch(e){ st.textContent='连接断开'; }
}
async function send(){
  const v=ta.value; if(!v.trim()) return;
  await fetch('/send',{method:'POST',body:v}); ta.value=''; ta.focus(); tick();
}
document.getElementById('send').onclick=send;
ta.addEventListener('keydown',e=>{ if((e.metaKey||e.ctrlKey)&&e.key==='Enter'){e.preventDefault();send();} });
setInterval(tick,700); tick();
</script></html>
""";

    private static int PickPort(int start)
    {
        for (var port = start; port < start + 20; port++)
        {
            try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return port; }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("找不到可用本地端口");
    }
}
