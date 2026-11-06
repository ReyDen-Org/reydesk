using ReyDesk.Windows.Transfer;
using ReyDesk.Protocol.V1;
using Google.Protobuf;
using System.Security.Cryptography;
var count = 0;
void Check(bool condition,string name) { if(!condition) throw new Exception(name); count++; Console.WriteLine("PASS: "+name); }
Check(!FileTransferEngine.SafeName("../escape"),"reject traversal");
Check(!FileTransferEngine.SafeName("C:\\escape"),"reject absolute paths");
Check(!FileTransferEngine.SafeName("secret\n.txt"),"reject control chars");
Check(FileTransferEngine.SafeName("آزمایش.txt"),"unicode display filename supported");
FileTransferOffer Offer(string id,byte[] data) => new() { TransferId=id,FileName="test.bin", FileSizeBytes=(ulong)data.Length,Sha256Hex=Convert.ToHexString(SHA256.HashData(data)) };
var folderFixture = Path.Combine(Path.GetTempPath(), "reydesk-folder-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folderFixture);
try {
    File.WriteAllText(Path.Combine(folderFixture, "existing.txt"), "keep");
    using (var first = ReceiveFolderPolicy.CreateUnique(folderFixture, "existing.txt", out var firstPath)) {
        Check(Path.GetFileName(firstPath) == "existing (1).txt", "remembered receive folder selects unused duplicate name");
        first.WriteByte(42);
    }
    Check(File.ReadAllText(Path.Combine(folderFixture, "existing.txt")) == "keep", "remembered folder preserves existing file bytes");
    using (var second = ReceiveFolderPolicy.CreateUnique(folderFixture, "existing.txt", out var secondPath))
        Check(Path.GetFileName(secondPath) == "existing (2).txt", "successive duplicates never overwrite previous delivery");
    Check(!ReceiveFolderPolicy.SafeName("CON.txt") && !ReceiveFolderPolicy.SafeName("COM1") && !ReceiveFolderPolicy.SafeName("trailing."), "Windows device aliases and trailing dots rejected");
    var rejected = false;
    try { using var invalid = ReceiveFolderPolicy.CreateUnique(folderFixture, "../escape", out _); } catch(IOException) { rejected = true; }
    Check(rejected, "remembered receive folder rejects traversal");
    rejected = false;
    try { using var invalid = ReceiveFolderPolicy.CreateUnique(Path.Combine(folderFixture, "missing"), "file.txt", out _); } catch(IOException) { rejected = true; }
    Check(rejected, "missing remembered folder cannot silently choose another destination");
} finally { foreach (var fixture in Directory.GetFiles(folderFixture)) File.Delete(fixture); Directory.Delete(folderFixture); }
var payload=new byte[50001];RandomNumberGenerator.Fill(payload);
Check(FileTransferEngine.ValidOffer(Offer("one",payload)),"valid bounded offer");
var large=Offer("two",payload);large.FileSizeBytes=FileTransferEngine.MaximumBytes+1;
Check(!FileTransferEngine.ValidOffer(large),"reject oversized offer");
FileTransferEngine? a=null,b=null;
MemoryStream? output=null;
a=new(e=>{_=b!.HandleAsync(e);return true;},()=>true,_=>Task.FromResult<TransferDestination?>(null));
b=new(e=>{_=a!.HandleAsync(e);return true;},()=>true,_=>Task.FromResult<TransferDestination?>(new(()=>output=new MemoryStream(),()=>{})));
Check(await a.SendAsync(new MemoryStream(payload),"test.bin",payload.Length),"multi-chunk loopback completes with matching ACKs");
Check(output is not null && output.ToArray().SequenceEqual(payload),"verified destination exactly matches source");
a.Dispose();b.Dispose();
var opened=0;
using var malicious=new FileTransferEngine(_=>true,()=>true,_=>Task.FromResult<TransferDestination?>(new(()=>{opened++;return new MemoryStream();},()=>{})));
await malicious.HandleAsync(new(){FileTransferOffer=Offer("bad-hash",payload)});
await malicious.HandleAsync(new(){FileTransferChunk=new(){TransferId="bad-hash",Offset=0,Data=ByteString.CopyFrom(new byte[1]),Final=true}});
Check(opened==0,"size mismatch cannot open destination");
var bytes=new byte[]{1,2,3};
await malicious.HandleAsync(new(){FileTransferOffer=Offer("bad-offset",bytes)});
await malicious.HandleAsync(new(){FileTransferChunk=new(){TransferId="bad-offset",Offset=1,Data=ByteString.CopyFrom(bytes),Final=true}});
Check(opened==0,"out-of-order chunk cannot open destination");
await malicious.HandleAsync(new(){FileTransferOffer=Offer("wrong-hash",bytes)});
await malicious.HandleAsync(new(){FileTransferChunk=new(){TransferId="wrong-hash",Offset=0,Data=ByteString.CopyFrom(new byte[]{3,2,1}),Final=true}});
Check(opened==0,"checksum mismatch cannot open destination");
var allowed=true;
using var revoked=new FileTransferEngine(_=>true,()=>allowed,_=>Task.FromResult<TransferDestination?>(new(()=>{opened++;return new MemoryStream();},()=>{})));
await revoked.HandleAsync(new(){FileTransferOffer=Offer("revoked",bytes)});allowed=false;
await revoked.HandleAsync(new(){FileTransferChunk=new(){TransferId="revoked",Offset=0,Data=ByteString.CopyFrom(bytes),Final=true}});
Check(opened==0,"revoked trust prevents destination access");
var consentCalls=0;
using var replay=new FileTransferEngine(_=>true,()=>true,_=>{consentCalls++;return Task.FromResult<TransferDestination?>(null);});
await replay.HandleAsync(new(){FileTransferOffer=Offer("single-use",bytes)});
await replay.HandleAsync(new(){FileTransferOffer=Offer("single-use",bytes)});
Check(consentCalls==1,"replayed transfer ID cannot prompt again");

await malicious.HandleAsync(new(){FileTransferOffer=Offer("oversized-chunk",payload)});
await malicious.HandleAsync(new(){FileTransferChunk=new(){TransferId="oversized-chunk",Data=ByteString.CopyFrom(new byte[FileTransferEngine.ChunkBytes+1])}});
Check(opened==0,"oversized chunk cannot open destination");

var consent=new TaskCompletionSource<TransferDestination?>(TaskCreationOptions.RunContinuationsAsynchronously);
var lateOpened=0;
using var late=new FileTransferEngine(_=>true,()=>true,_=>consent.Task);
var pendingOffer=late.HandleAsync(new(){FileTransferOffer=Offer("late-consent",bytes)});
late.Cancel();
consent.SetResult(new(()=>{lateOpened++;return new MemoryStream();},()=>{}));
await pendingOffer.WaitAsync(TimeSpan.FromSeconds(2));
await late.HandleAsync(new(){FileTransferChunk=new(){TransferId="late-consent",Data=ByteString.CopyFrom(bytes),Final=true}});
Check(lateOpened==0,"consent resolving after cancellation cannot open destination");

var blocking=new BlockingDestination();var discarded=0;
using var canceled=new FileTransferEngine(_=>true,()=>true,_=>Task.FromResult<TransferDestination?>(new(()=>blocking,()=>discarded++)));
await canceled.HandleAsync(new(){FileTransferOffer=Offer("cancel-write",bytes)});
var writing=canceled.HandleAsync(new(){FileTransferChunk=new(){TransferId="cancel-write",Data=ByteString.CopyFrom(bytes),Final=true}});
await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
await canceled.HandleAsync(new(){FileTransferControl=new(){TransferId="cancel-write",RequestedState=FileTransferState.Canceled}}).WaitAsync(TimeSpan.FromSeconds(2));
await writing.WaitAsync(TimeSpan.FromSeconds(2));
Check(discarded==1,"remote cancel interrupts destination IO and discards only selected partial");

var openCleanup=0;
using var openFailure=new FileTransferEngine(_=>true,()=>true,_=>Task.FromResult<TransferDestination?>(new(()=>throw new IOException("created then failed"),()=>openCleanup++)));
await openFailure.HandleAsync(new(){FileTransferOffer=Offer("open-failure",bytes)});
await openFailure.HandleAsync(new(){FileTransferChunk=new(){TransferId="open-failure",Data=ByteString.CopyFrom(bytes),Final=true}});
Check(openCleanup==1,"destination open failure invokes created-file cleanup");

var writeAllowed=true;var forbidden=new MemoryStream();var revokeCleanup=0;
using var duringOpen=new FileTransferEngine(_=>true,()=>writeAllowed,_=>Task.FromResult<TransferDestination?>(new(()=>{writeAllowed=false;return forbidden;},()=>revokeCleanup++)));
await duringOpen.HandleAsync(new(){FileTransferOffer=Offer("revoke-at-open",bytes)});
await duringOpen.HandleAsync(new(){FileTransferChunk=new(){TransferId="revoke-at-open",Data=ByteString.CopyFrom(bytes),Final=true}});
Check(forbidden.ToArray().Length==0 && revokeCleanup==1,"owner revoked at open prevents every content write");

MemoryStream? emptyOutput=null;
using var empty=new FileTransferEngine(_=>true,()=>true,_=>Task.FromResult<TransferDestination?>(new(()=>emptyOutput=new MemoryStream(),()=>{})));
empty.Progress+=_=>throw new Exception("UI observer failed");
await empty.HandleAsync(new(){FileTransferOffer=Offer("empty",[])});
await empty.HandleAsync(new(){FileTransferChunk=new(){TransferId="empty",Final=true}});
Check(emptyOutput is not null && emptyOutput.ToArray().Length==0,"empty file verifies and UI observer failure cannot break protocol");
FileTransferEngine? badAck=null;
badAck=new(e=>
{
    if(e.FileTransferOffer is {} sentOffer) _=badAck!.HandleAsync(new(){FileTransferResponse=new(){TransferId=sentOffer.TransferId,Decision=FileTransferDecision.Accept}});
    if(e.FileTransferChunk is {} sentChunk) _=badAck!.HandleAsync(new(){FileTransferProgress=new(){TransferId=sentChunk.TransferId,BytesTransferred=999,State=FileTransferState.Completed}});
    return true;
},()=>true,_=>Task.FromResult<TransferDestination?>(null));
using(badAck) Check(!await badAck.SendAsync(new MemoryStream(bytes),"ack.bin",bytes.Length),"sender rejects acknowledgement with incorrect byte count");
var offered=0;
using var changedSource=new FileTransferEngine(e=>{if(e.FileTransferOffer is not null)offered++;return true;},()=>true,_=>Task.FromResult<TransferDestination?>(null));
Check(!await changedSource.SendAsync(new MemoryStream(bytes),"changed.bin",bytes.Length-1) && offered==0,"source size mismatch fails before sending an offer");
Console.WriteLine($"PASS {count} file transfer checks");

sealed class BlockingDestination : MemoryStream
{
    public TaskCompletionSource<bool> Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.Infinite,cancellationToken);
    }
}
