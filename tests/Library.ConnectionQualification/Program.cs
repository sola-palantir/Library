using System.Text.Json;
using Library.Companion;
using Library.Execution;

// No enrollment or credential persistence. Public test token; synthetic folder only.
// The isolated API must be provisioned for this short-lived synthetic fixture.
if (args.Length != 1) throw new ArgumentException("Supply the isolated synthetic fixture configuration.");
var config=JsonSerializer.Deserialize<ReviewAccessBundle>(File.ReadAllText(args[0]),ExecutionContract.Json) ?? throw new ArgumentException("Missing fixture.");
if (config.Token != new string('a',64) || config.Profile.ExpiresUtc-DateTimeOffset.UtcNow > TimeSpan.FromMinutes(10) || !File.Exists(Path.Combine(config.Profile.Folder,"synthetic-qualification.txt")) || File.ReadAllText(Path.Combine(config.Profile.Folder,"synthetic-qualification.txt")) != "Library synthetic connection qualification only") throw new ArgumentException("Only an explicitly marked synthetic short-lived fixture is allowed.");
var bytes=System.Text.Encoding.UTF8.GetBytes("public synthetic bytes; not a credential");
var encrypted=ReviewAccess.Protect(bytes);
if (!ReviewAccess.Protect(encrypted,true).SequenceEqual(bytes)) throw new IOException("Current-user DPAPI round trip failed.");
encrypted[0]^=255; var damageRejected=false;
try { ReviewAccess.Protect(encrypted,true); } catch { damageRejected=true; }
if (!damageRejected) throw new IOException("Damaged DPAPI blob was not rejected.");
Console.WriteLine("Current-user DPAPI synthetic in-memory round-trip and damage rejection passed. No persistent credential created.");
using var cancelled = new CancellationTokenSource(TimeSpan.FromMinutes(9)); Console.CancelKeyPress += (_,e)=>{e.Cancel=true;cancelled.Cancel();};
try { await new ReviewConnectionClient(config).Run(cancelled.Token); }
catch (IOException e) when (e.Message.Contains("(401)")) { Console.WriteLine("Synthetic authorization was revoked or expired; worker stopped."); }
