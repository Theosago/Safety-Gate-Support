using SafetyGateSupport.Hokuyo;
using System.Text;
using System.IO.Compression;
var checks = new (string Name, Action Check)[]
{
    ("display codes are hex and normalize lowercase segment b/d", () => { Require(UamErrorCatalog.Lookup(" b1 ").Code == "B1", "Lowercase display was not normalized."); Require(UamErrorCatalog.Lookup("0x5d").Title == "Encoder speed error", "Hex prefix was misinterpreted."); }),
    ("the manual's supply and interference codes retain different meanings", () => { Require(UamErrorCatalog.Lookup("74").Title == "Power-supply error", "Supply code misdecoded."); Require(UamErrorCatalog.Lookup("70").Title.StartsWith("Optical interference"), "Interference code misdecoded."); }),
    ("zero and unlisted display states are never treated as healthy", () => { Require(!UamErrorCatalog.Lookup("00").IsListed && !UamErrorCatalog.Lookup("01").IsListed, "Unlisted state was classified as a known error or normal status."); }),
    ("OSSD range and separate diagnostic codes are included", () => { foreach (string code in new[]{"95","96","97","98","99","9A","A8","AC"}) Require(UamErrorCatalog.Lookup(code).Title == "OSSD diagnostic error", "OSSD code missing: " + code); }),
    ("range boundaries do not swallow other named errors", () => { Require(UamErrorCatalog.Lookup("80").Title == "Master-unit error", "Master error became generic."); Require(UamErrorCatalog.Lookup("C0").Title == "Optical-window contamination" && UamErrorCatalog.Lookup("C1").Title.StartsWith("Optical window removed"), "Window range boundary incorrect."); Require(UamErrorCatalog.Lookup("D3").Title == "Incomplete configuration" && UamErrorCatalog.Lookup("E6").Title == "Incomplete configuration" && !UamErrorCatalog.Lookup("E7").Title.StartsWith("Incomplete"), "Configuration range boundary incorrect."); }),
    ("invalid code strings are rejected without decimal guessing", () => { foreach (string value in new[]{"", "1", "123", "G1", "7 4", "0x123", "-1"}) { try { UamErrorCatalog.Lookup(value); throw new Exception("Accepted invalid code: " + value); } catch(FormatException) { } } }),
    ("all byte values have a source and a conservative interpretation", () => { for (int n=0;n<256;n++) { var info=UamErrorCatalog.Lookup(n.ToString("X2")); Require(info.Title.Length > 0 && info.Checks.Length > 0 && info.Source.Contains("C-61-00003-12"), "Missing lookup detail."); } }),
    ("discovery accepts COM59 with both vendor USB IDs and driver child interfaces", KnownUsbCandidates),
    ("discovery retains unidentified USB interfaces without claiming scanner identity", UnknownUsbCandidates),
    ("discovery excludes non-USB, invalid COM names and duplicate ports", DiscoveryFiltering),
    ("automatic selection prefers one recognized scanner and preserves the exact selection", AutomaticSelection),
    ("ambiguous connections are never auto-selected or probed", AmbiguousSelection),
    ("identity request matches the independently checked native CRC vector", () => Require(Convert.ToHexString(UamWire.LegacyIdentityRequest()) == "0230303045565230303334393203", "VR00 wire vector changed.")),
    ("only fixed read requests are exposed", () => { Require(Encoding.ASCII.GetString(UamWire.StatusRequest()).Contains("AR00"), "Wrong status request."); Require(UamWire.StatusRequest().Length == 14, "Unexpected request payload."); }),
    ("framing rejects corrupt CRC, mismatched lengths and wrong commands", WireFailures),
    ("unknown and truncated status fields cannot become a healthy reading", UnknownStatus),
    ("active fault and past code stay separate", () => { var past = UamStatus.Parse("0010740000000"); Require(!past.ErrorActive && past.LastErrorCode == "74" && past.Describe().Contains("not being classified as a current fault"), "Stored fault mislabeled active."); var active=UamStatus.Parse("0011741000000"); Require(active.ErrorActive && active.Lockout, "Active fault lost."); }),
    ("fragmented replies complete with exactly two fixed read requests", SuccessfulSession),
    ("wrong model and ambiguous model strings prevent AR00", ModelMismatch),
    ("protocol rejection stops without retries", RejectedIdentity),
    ("both real working and faulty reports are identical valid empty rejections", CapturedRejection),
    ("VR42 supports the native normalized VR00 version reply", NormalizedVersionReply),
    ("legacy identity is queried once only after empty status 90", LegacyFallback),
    ("a double identity rejection never becomes a physical fault diagnosis", DoubleRejection),
    ("status 90 carrying unexpected data cannot trigger a fallback", RejectionWithData),
    ("cancel before start never opens USB", CanceledSession),
    ("existing or unwritable report prevents opening USB", ReportFailure),
    ("partial replies are retained and never treated as successful", PartialReply),
    ("failed USB close invalidates an otherwise completed read", CloseFailure)
};
int passed=0;
foreach(var (name, check) in checks) { try {check();passed++;Console.WriteLine("PASS "+name);} catch(Exception error){Console.Error.WriteLine($"FAIL {name}: {error}");} }
Console.WriteLine($"Hokuyo checks: {passed}/{checks.Length} passed.");return passed==checks.Length?0:1;
static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}

static UamSerialDevice Device(string port, string usb, string label="USB serial device", string instance="") => new(port, instance.Length==0 ? usb : instance, usb, label, "");
static void KnownUsbCandidates()
{
    var virtualDriver=UamDeviceDiscovery.FindCandidates([Device("COM59","","HOKUYO UAM-05LP USB Driver",@"ROOT\vendor-serial\test")]);
    Require(virtualDriver.Count==1 && virtualDriver[0].IsRecognizedCandidate,"Named vendor virtual interface excluded without a USB parent.");
    foreach(string id in new[]{@"USB\VID_143F&PID_0021\test",@"USB\VID_15D1&PID_0000&MI_00\test"})
    {
        var devices=UamDeviceDiscovery.FindCandidates([Device("COM59",id,instance:@"FTDIBUS\child")]);
        Require(devices.Count==1&&devices[0].PortName=="COM59"&&devices[0].IsRecognizedCandidate,"Vendor driver or high COM number excluded.");
    }
}
static void UnknownUsbCandidates()
{
    var devices=UamDeviceDiscovery.FindCandidates([Device("COM59",@"USB\VID_ABCD&PID_1234\test")]);
    Require(devices.Count==1&&!devices[0].IsRecognizedCandidate&&devices[0].DisplayName.Contains("identity not checked"),"Unknown USB device hidden or called a scanner.");
    Require(UamDeviceDiscovery.AutoSelect(devices,null)==devices[0],"Sole connected USB device not selected.");
}
static void DiscoveryFiltering()
{
    var devices=UamDeviceDiscovery.FindCandidates([Device("COM59",@"USB\VID_143F&PID_0021\test"),Device("com59",@"USB\VID_143F&PID_0021\duplicate"),Device("COM1",@"ACPI\PNP0501\test"),Device("COM0",@"USB\VID_ABCD&PID_1234\test"),Device("COM59evil",@"USB\VID_ABCD&PID_1234\test"),Device("LPT1",@"USB\VID_ABCD&PID_1234\test")]);
    Require(devices.Count==1,"Non-USB, malformed or duplicate port accepted.");
}
static void AutomaticSelection()
{
    var devices=UamDeviceDiscovery.FindCandidates([Device("COM3",@"USB\VID_ABCD&PID_1234\test"),Device("COM59",@"USB\VID_143F&PID_0021\test")]);
    Require(UamDeviceDiscovery.AutoSelect(devices,null)?.PortName=="COM59","Recognized scanner was not preferred.");
    Require(UamDeviceDiscovery.AutoSelect(devices,devices.Single(d=>d.PortName=="COM3"))?.PortName=="COM3","Explicit selection lost during refresh.");
    var renamed=devices.Select(d=>d with{PortName="COM99"}).ToArray();
    Require(UamDeviceDiscovery.AutoSelect(renamed,devices[0])?.PortName=="COM99","Stale port selected after reconnection.");
}
static void AmbiguousSelection()
{
    var unknown=UamDeviceDiscovery.FindCandidates([Device("COM3",@"USB\VID_ABCD&PID_1234\a"),Device("COM59",@"USB\VID_ABCD&PID_1234\b")]);
    Require(UamDeviceDiscovery.AutoSelect(unknown,null)==null,"Ambiguous USB device selected.");
    var known=UamDeviceDiscovery.FindCandidates([Device("COM3",@"USB\VID_143F&PID_0021\a"),Device("COM59",@"USB\VID_143F&PID_0021\b")]);
    Require(UamDeviceDiscovery.AutoSelect(known,null)==null,"One of two scanners selected arbitrarily.");
    Require(UamDeviceDiscovery.AutoSelect([],known[0])==null,"Disconnected scanner retained.");
}

static byte[] Reply(string command, string data, string status="00")
{
    // Synthetic fixtures exercise parsing, not hardware compatibility.
    string body=(16+data.Length).ToString("X4")+command+status+data;
    return Encoding.ASCII.GetBytes("\x02"+body+UamWire.Crc(Encoding.ASCII.GetBytes(body)).ToString("X4")+"\x03");
}
static void WireFailures()
{
    var good=Reply("VR00", "UAM-05LP-T301,1.0,TEST");
    Require(UamWire.Decode(good,"VR00").Data.StartsWith("UAM-"),"Good fixture failed.");
    foreach(int offset in new[]{0,2,9,good.Length-2,good.Length-1}) { var bad=good.ToArray();bad[offset]^=1; Throws<InvalidDataException>(()=>UamWire.Decode(bad,"VR00")); }
    Throws<InvalidDataException>(()=>UamWire.Decode(good,"AR00"));
    Throws<InvalidDataException>(()=>UamWire.Decode(new byte[65536],"VR00"));
}
static void UnknownStatus()
{
    foreach(string s in new[]{"", "001074", "2010740000000", "0012740000000", "0010742000000", "0FF0740000000", "0010GG0000000"}) Throws<InvalidDataException>(()=>UamStatus.Parse(s));
    Require(UamStatus.Parse("0010000000000").Describe().Contains("does not establish"), "No fault was equated with function.");
}
static void SuccessfulSession()
{
    WithFolder(folder=> { var fake=new FakeUsb(Reply("VR42","UAM-05LP-T301,1.0,TEST").Concat(Reply("AR00","0010740000000")).ToArray()); var path=Path.Combine(folder,"report.zip"); var result=Run(fake,path);Require(result.Success && fake.Closed,"Read incomplete or port not closed.");Require(fake.Sent.SequenceEqual(UamWire.IdentityRequest().Concat(UamWire.StatusRequest())),"Unexpected commands sent."); using var zip=ZipFile.OpenRead(path);Require(zip.GetEntry("result.json")!=null && zip.GetEntry("AR00-response.bin")!=null,"Evidence missing."); });
}
static void ModelMismatch()
{
    foreach(string name in new[]{"URG-04LX","UAM-05LP-T301-OTHER","unknown UAM-05LP-T301","UAM-05LP"}) WithFolder(folder=> {var fake=new FakeUsb(Reply("VR42",name+",1.0,TEST")); Require(!Run(fake,Path.Combine(folder,"r.zip")).Success,"Wrong model accepted.");Require(fake.Sent.SequenceEqual(UamWire.IdentityRequest()),"AR00 sent to wrong model.");});
}
static void RejectedIdentity() {WithFolder(folder=> {var fake=new FakeUsb(Reply("VR42","","37"));Require(!Run(fake,Path.Combine(folder,"r.zip")).Success,"Rejection accepted.");Require(fake.Sent.Count==14,"Retried rejected query.");});}
static void CapturedRejection()
{
    // Both user captures, working and faulty, contain this exact 16-byte reply.
    var raw=Convert.FromHexString("02303031305652303039303644323303");
    var reply=UamWire.Decode(raw,"VR00");
    Require(reply.Status=="90"&&reply.Data=="","Real rejection was misdecoded as data or a scanner condition.");
    var startup=Encoding.ASCII.GetString(UamWire.IdentityRequest());
    Require(startup.Substring(1,8)=="000EVR42","Vendor startup identity query missing.");
}
static void NormalizedVersionReply()
{
    var reply=UamWire.Decode(Reply("VR00","UAM-05LP-T301,1.0,TEST"),"VR42");
    Require(reply.Command=="VR00"&&reply.Status=="00","Normalized VR version reply rejected.");
    Throws<InvalidDataException>(()=>UamWire.Decode(Reply("AR00","0010000000000"),"VR42"));
    Throws<InvalidDataException>(()=>UamWire.Decode(Reply("VR99",""),"VR42"));
}
static void LegacyFallback()
{
    WithFolder(folder=> {var fake=new FakeUsb(Reply("VR42","","90").Concat(Reply("VR00","UAM-05LP-T301,1.0,TEST")).Concat(Reply("AR00","0010740000000")).ToArray());
        var result=Run(fake,Path.Combine(folder,"r.zip"));Require(result.Success,"Explicit legacy fallback failed.");
        Require(fake.Sent.SequenceEqual(UamWire.IdentityRequest().Concat(UamWire.LegacyIdentityRequest()).Concat(UamWire.StatusRequest())),"Fallback sent unexpected commands.");});
}
static void DoubleRejection()
{
    WithFolder(folder=> {var fake=new FakeUsb(Reply("VR42","","90").Concat(Convert.FromHexString("02303031305652303039303644323303")).ToArray());
        var result=Run(fake,Path.Combine(folder,"r.zip"));Require(!result.Success&&result.Summary.Contains("not a scanner display fault code"),"Rejection misclassified as fault diagnosis.");
        Require(fake.Sent.SequenceEqual(UamWire.IdentityRequest().Concat(UamWire.LegacyIdentityRequest())),"AR00 or extra retries sent after double rejection.");});
}
static void RejectionWithData()
{
    WithFolder(folder=> {var fake=new FakeUsb(Reply("VR42","unexpected","90"));Require(!Run(fake,Path.Combine(folder,"r.zip")).Success,"Rejection with data accepted.");Require(fake.Sent.SequenceEqual(UamWire.IdentityRequest()),"Unexpected fallback on malformed rejection.");});
}
static void CanceledSession() {WithFolder(folder=> {using var c=new CancellationTokenSource();c.Cancel();bool opened=false;var r=UamReadSession.Run(new("COM1","test","test"),Path.Combine(folder,"r.zip"),_=>{opened=true;return new MemoryStream();},c.Token,_=>{});Require(!opened&&!r.Success,"Canceled operation opened USB.");});}
static void ReportFailure() {WithFolder(folder=> {string p=Path.Combine(folder,"r.zip");File.WriteAllText(p,"old");bool opened=false;Throws<IOException>(()=>UamReadSession.Run(new("COM1","test","test"),p,_=>{opened=true;return new MemoryStream();},default,_=>{}));Require(!opened&&File.ReadAllText(p)=="old","Existing report damaged or USB opened.");});}
static void PartialReply() {WithFolder(folder=> {var fake=new FakeUsb(Reply("VR42","UAM-05LP-T301,1.0,TEST")[..10]);string p=Path.Combine(folder,"r.zip");Require(!Run(fake,p).Success,"Partial reply accepted.");using var z=ZipFile.OpenRead(p);Require(z.GetEntry("VR42-response.bin")?.Length==10,"Partial evidence lost.");});}
static void CloseFailure() {WithFolder(folder=> {var fake=new FakeUsb(Reply("VR42","UAM-05LP-T301,1.0,TEST").Concat(Reply("AR00","0010000000000")).ToArray()){FailClose=true};var r=Run(fake,Path.Combine(folder,"r.zip"));Require(!r.Success&&r.Summary.Contains("close failed"),"Close failure hidden.");});}
static UamReadResult Run(FakeUsb fake,string path)=>UamReadSession.Run(new("COM1","test","test"),path,_=>fake,default,_=>{});
static void WithFolder(Action<string> action){string p=Path.Combine(Path.GetTempPath(),"uam-validation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);try{action(p);}finally{Directory.Delete(p,true);}}
static void Throws<T>(Action action) where T:Exception {try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
sealed class FakeUsb(byte[] response):Stream
{
    int offset;public List<byte> Sent{get;}=[];public bool Closed{get;private set;}public bool FailClose{get;init;}
    public override int Read(byte[] buffer,int start,int count){int n=Math.Min(Math.Min(count,3),response.Length-offset);response.AsSpan(offset,n).CopyTo(buffer.AsSpan(start));offset+=n;return n;}
    public override void Write(byte[] buffer,int start,int count)=>Sent.AddRange(buffer.AsSpan(start,count).ToArray());
    protected override void Dispose(bool disposing){Closed=true;if(FailClose)throw new IOException("test close failure");base.Dispose(disposing);}
    public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}public override void Flush(){}public override long Seek(long a,SeekOrigin b)=>throw new NotSupportedException();public override void SetLength(long a)=>throw new NotSupportedException();
}
