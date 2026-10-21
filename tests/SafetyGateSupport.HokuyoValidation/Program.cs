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
    ("identity request matches the independently checked native CRC vector", () => Require(Convert.ToHexString(UamWire.IdentityRequest()) == "0230303045565230303334393203", "VR00 wire vector changed.")),
    ("only fixed read requests are exposed", () => { Require(Encoding.ASCII.GetString(UamWire.StatusRequest()).Contains("AR00"), "Wrong status request."); Require(UamWire.StatusRequest().Length == 14, "Unexpected request payload."); }),
    ("framing rejects corrupt CRC, mismatched lengths and wrong commands", WireFailures),
    ("unknown and truncated status fields cannot become a healthy reading", UnknownStatus),
    ("active fault and past code stay separate", () => { var past = UamStatus.Parse("0010740000000"); Require(!past.ErrorActive && past.LastErrorCode == "74" && past.Describe().Contains("not being classified as a current fault"), "Stored fault mislabeled active."); var active=UamStatus.Parse("0011741000000"); Require(active.ErrorActive && active.Lockout, "Active fault lost."); }),
    ("fragmented replies complete with exactly two fixed read requests", SuccessfulSession),
    ("wrong model and ambiguous model strings prevent AR00", ModelMismatch),
    ("protocol rejection stops without retries", RejectedIdentity),
    ("cancel before start never opens USB", CanceledSession),
    ("existing or unwritable report prevents opening USB", ReportFailure),
    ("partial replies are retained and never treated as successful", PartialReply),
    ("failed USB close invalidates an otherwise completed read", CloseFailure)
};
int passed=0;
foreach(var (name, check) in checks) { try {check();passed++;Console.WriteLine("PASS "+name);} catch(Exception error){Console.Error.WriteLine($"FAIL {name}: {error}");} }
Console.WriteLine($"Hokuyo checks: {passed}/{checks.Length} passed.");return passed==checks.Length?0:1;
static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}

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
    WithFolder(folder=> { var fake=new FakeUsb(Reply("VR00","UAM-05LP-T301,1.0,TEST").Concat(Reply("AR00","0010740000000")).ToArray()); var path=Path.Combine(folder,"report.zip"); var result=Run(fake,path);Require(result.Success && fake.Closed,"Read incomplete or port not closed.");Require(fake.Sent.SequenceEqual(UamWire.IdentityRequest().Concat(UamWire.StatusRequest())),"Unexpected commands sent."); using var zip=ZipFile.OpenRead(path);Require(zip.GetEntry("result.json")!=null && zip.GetEntry("AR00-response.bin")!=null,"Evidence missing."); });
}
static void ModelMismatch()
{
    foreach(string name in new[]{"URG-04LX","UAM-05LP-T301-OTHER","unknown UAM-05LP-T301","UAM-05LP"}) WithFolder(folder=> {var fake=new FakeUsb(Reply("VR00",name+",1.0,TEST")); Require(!Run(fake,Path.Combine(folder,"r.zip")).Success,"Wrong model accepted.");Require(fake.Sent.SequenceEqual(UamWire.IdentityRequest()),"AR00 sent to wrong model.");});
}
static void RejectedIdentity() {WithFolder(folder=> {var fake=new FakeUsb(Reply("VR00","","37"));Require(!Run(fake,Path.Combine(folder,"r.zip")).Success,"Rejection accepted.");Require(fake.Sent.Count==14,"Retried rejected query.");});}
static void CanceledSession() {WithFolder(folder=> {using var c=new CancellationTokenSource();c.Cancel();bool opened=false;var r=UamReadSession.Run(new("COM1","test","test"),Path.Combine(folder,"r.zip"),_=>{opened=true;return new MemoryStream();},c.Token,_=>{});Require(!opened&&!r.Success,"Canceled operation opened USB.");});}
static void ReportFailure() {WithFolder(folder=> {string p=Path.Combine(folder,"r.zip");File.WriteAllText(p,"old");bool opened=false;Throws<IOException>(()=>UamReadSession.Run(new("COM1","test","test"),p,_=>{opened=true;return new MemoryStream();},default,_=>{}));Require(!opened&&File.ReadAllText(p)=="old","Existing report damaged or USB opened.");});}
static void PartialReply() {WithFolder(folder=> {var fake=new FakeUsb(Reply("VR00","UAM-05LP-T301,1.0,TEST")[..10]);string p=Path.Combine(folder,"r.zip");Require(!Run(fake,p).Success,"Partial reply accepted.");using var z=ZipFile.OpenRead(p);Require(z.GetEntry("VR00-response.bin")?.Length==10,"Partial evidence lost.");});}
static void CloseFailure() {WithFolder(folder=> {var fake=new FakeUsb(Reply("VR00","UAM-05LP-T301,1.0,TEST").Concat(Reply("AR00","0010000000000")).ToArray()){FailClose=true};var r=Run(fake,Path.Combine(folder,"r.zip"));Require(!r.Success&&r.Summary.Contains("close failed"),"Close failure hidden.");});}
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
