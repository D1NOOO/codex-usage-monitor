[CmdletBinding()]
param([string]$MonitorPath = '', [string]$LegacyMonitorPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
if ($LegacyMonitorPath) { $LegacyMonitorPath = [IO.Path]::GetFullPath($LegacyMonitorPath) }
$testDirectory = Join-Path $repoRoot '.build\scale-migration-test'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$source = Join-Path $testDirectory 'ScaleMigrationTest.cs'
$testExe = Join-Path $testDirectory 'ScaleMigrationTest.exe'
@'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class ScaleMigrationTest
{
    private const BindingFlags Instance = BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int assertions, pixelCases;
    private static Type Settings, Renderer;
    private static object Get(object value,string name) { return value.GetType().GetProperty(name).GetValue(value,null); }
    private static void Set(object value,string name,object next) { value.GetType().GetProperty(name).SetValue(value,next,null); }
    private static object Call(object value,string name,params object[] args) { return value.GetType().GetMethod(name,Instance).Invoke(value,args); }
    private static void Check(bool value,string name) { if(!value) throw new Exception(name); assertions++; }
    private static object Parse(string text) { return Settings.GetMethod("Parse",Static).Invoke(null,new object[]{text}); }
    private static double Scale(object settings) { return (double)Get(Get(settings,"Style"),"Scale"); }
    private static string Legacy(double scale) {
        return "{\"Language\":\"en\",\"OverlayMode\":\"desktop\",\"DesktopX\":123,\"DesktopY\":234,\"RefreshSeconds\":90,"+
            "\"DiagnosticsEnabled\":true,\"Style\":{\"Scale\":"+scale.ToString("R",CultureInfo.InvariantCulture)+
            ",\"FontSize\":17,\"ResetFontSize\":12,\"Opacity\":0.8,\"CornerRadius\":14,\"Background\":\"#123456\"}}";
    }
    private static Size Pixels(object settings,int dpi,bool credits) {
        return (Size)Renderer.GetMethod("GetPixelSize").Invoke(null,new object[]{settings,credits,dpi});
    }
    [STAThread]
    private static void Main(string[] args) {
        try {
            Assembly current=Assembly.LoadFile(Path.GetFullPath(args[0]));
            Settings=current.GetType("CodexRateMonitorNative.MonitorSettings",true);
            Renderer=current.GetType("CodexRateMonitorNative.OverlayRenderer",true);
            MigrationTests(current);
            UiTests(current);
            UpdateTests(current);
            if(args.Length>1 && !string.IsNullOrEmpty(args[1])) PixelTests(current,Assembly.LoadFile(Path.GetFullPath(args[1])));
            Console.WriteLine("PASS "+assertions+" scale baseline/migration assertions; "+pixelCases+" old/new bitmap comparisons.");
        } catch(Exception ex) {Console.Error.WriteLine("FAIL "+ex);Environment.ExitCode=1;}
    }
    private static void MigrationTests(Assembly current) {
        object fresh=Activator.CreateInstance(Settings,true);
        Check(Scale(fresh)==1 && (int)Get(Get(fresh,"Style"),"ScaleBasisVersion")==2,"new settings must be current-basis 100%");
        Check(Pixels(fresh,96,true)==new Size(529,34),"new 100% must render at the former 85% size");
        foreach(double legacy in new[]{0.75,0.82,0.85,1d,1.35,1.5}) {
            object settings=Parse(Legacy(legacy));double scale=Scale(settings);
            Check(Math.Abs(scale*0.85-legacy)<1e-12,"migration changed effective scale "+legacy);
            Check((int)Get(Get(settings,"Style"),"ScaleBasisVersion")==2,"migration must record its basis");
            Check((int)Get(settings,"DesktopX")==123 && (int)Get(settings,"DesktopY")==234 &&
                (int)Get(settings,"RefreshSeconds")==90 && (bool)Get(settings,"DiagnosticsEnabled") &&
                (double)Get(Get(settings,"Style"),"FontSize")==17 && (string)Get(Get(settings,"Style"),"Background")=="#123456",
                "migration changed unrelated preferences");
            foreach(string lines in new[]{"1","2"}) foreach(bool credits in new[]{false,true}) foreach(int dpi in new[]{96,120,144,168,192,240}) {
                Set(settings,"DisplayLines",lines);
                int width=lines=="2"?252:credits?622:470,height=lines=="2"?credits?106:78:40;
                float oldFactor=(float)(legacy*dpi/96d);
                Check(Pixels(settings,dpi,credits)==new Size((int)Math.Round(width*oldFactor),(int)Math.Round(height*oldFactor)),
                    "existing dimensions changed after migration at "+dpi+" DPI");
            }
            for(int i=0;i<10;i++) {settings=Parse(Json.Serialize(settings));settings=Call(settings,"Clone");}
            Check(Scale(settings)==scale,"repeated deserialize/clone must not compound migration");
        }
        Check(Math.Abs(Scale(Parse("{\"style\":{\"scale\":0.85}}"))-1)<1e-12,"lowercase legacy style must migrate");
        Check(Scale(Parse("{\"style\":{\"scaleBasisVersion\":2,\"scale\":0.82}}"))==0.82,"new lowercase templates must retain their current-basis scale");
        Check(Math.Abs(Scale(Parse("{\"style\":{\"scaleBasisVersion\":1,\"scale\":1}}"))*0.85-1)<1e-12,"explicit legacy basis must migrate once");
        Check(Math.Abs(Scale(Parse(Legacy(0.1)))*0.85-0.75)<1e-12 && Math.Abs(Scale(Parse(Legacy(3)))*0.85-1.5)<1e-12,
            "out-of-range legacy values retain their formerly clamped physical size");
        Check(Math.Abs(Scale(Parse("{\"RefreshSeconds\":60}"))*0.85-1)<1e-12,"old partial settings use the original release's implicit scale");
        object currentSettings=Parse("{\"Style\":{\"ScaleBasisVersion\":2,\"Scale\":0.5}}");
        Check(Scale(currentSettings)==0.5,"versioned 50% must remain 50%");
        object style=Get(currentSettings,"Style");
        Set(style,"Scale",0.1);Call(style,"Normalize");Check((double)Get(style,"Scale")==0.5,"minimum is 50%");
        Set(style,"Scale",3d);Call(style,"Normalize");Check((double)Get(style,"Scale")==2,"maximum is 200%");
        Set(style,"Scale",double.NaN);Call(style,"Normalize");Check((double)Get(style,"Scale")==1,"invalid numeric scale cannot reach drawing");
        string settingsFile=(string)Settings.GetProperty("SettingsPath").GetValue(null,null),oldText=Legacy(1);
        File.WriteAllText(settingsFile,oldText);
        object loaded=Settings.GetMethod("Load").Invoke(null,null);
        Check(File.ReadAllText(settingsFile)==oldText,"loading must not overwrite existing settings");
        double before=Scale(loaded);Call(loaded,"Save");
        loaded=Settings.GetMethod("Load").Invoke(null,null);
        Check(Scale(loaded)==before && File.ReadAllText(settingsFile).Contains("\"ScaleBasisVersion\":2"),"save/restart must persist the migrated basis without size drift");
        File.Delete(settingsFile);
        Check(Scale(Settings.GetMethod("Load").Invoke(null,null))==1,"a fresh install without a settings file uses new 100%");
    }
    private static void UiTests(Assembly assembly) {
        Type formType=assembly.GetType("CodexRateMonitorNative.AppearanceSettingsForm",true);
        foreach(string language in new[]{"zh-CN","zh-TW","en"}) {
            assembly.GetType("CodexRateMonitorNative.I18n",true).GetMethod("SetLanguage").Invoke(null,new object[]{language});
            object settings=Parse(Legacy(1));Set(settings,"Language",language);double exact=Scale(settings);
            using(var form=(Form)Activator.CreateInstance(formType,new object[]{settings,null,null,null})) {
                var input=(NumericUpDown)formType.GetField("scale",Instance).GetValue(form);
                Check(input.Minimum==50 && input.Maximum==200 && input.Increment==1 && input.DecimalPlaces==2,"scale editor range/precision mismatch");
                Check(input.Value==117.65m,"legacy 100% should display new 117.65%");
                ((NumericUpDown)formType.GetField("opacity",Instance).GetValue(form)).Value=75;
                Call(form,"UpdateWorking");
                object working=formType.GetField("working",Instance).GetValue(form);
                Check(Scale(working)==exact && (double)Get(Get(working,"Style"),"Opacity")==0.75,"unrelated edits must not round migrated scale");
                Call(form,"LoadControls");Call(form,"UpdateWorking");
                Check(Scale(working)==exact,"reloading and saving controls must retain precise scale");
                input.Value=50;Call(form,"UpdateWorking");
                Check(Scale(working)==0.5,"user can select 50% through the editor");
                input.Value=200;Call(form,"UpdateWorking");Check(Scale(working)==2,"user can select 200% through the editor");
            }
        }
    }
    private static void UpdateTests(Assembly assembly) {
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-"+Guid.NewGuid().ToString("N"));
        string source=Path.Combine(root,"source"),destination=Path.Combine(root,"destination");Directory.CreateDirectory(source);Directory.CreateDirectory(destination);
        string defaults=Json.Serialize(Activator.CreateInstance(Settings,true)),legacy=Legacy(1.5);
        File.WriteAllText(Path.Combine(source,"settings.json"),defaults);File.WriteAllText(Path.Combine(destination,"settings.json"),legacy);
        assembly.GetType("CodexRateMonitorNative.UpdateInstaller",true).GetMethod("CopyPayload",Static).Invoke(null,new object[]{source,destination});
        string actual=File.ReadAllText(Path.Combine(destination,"settings.json"));
        Check(actual==legacy && Math.Abs(Scale(Parse(actual))*0.85-1.5)<1e-12,"updater must preserve existing preferences and maximum legacy size");
    }
    private static object Sample(Assembly assembly) {
        object snapshot=Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.RateSnapshot",true),true);
        foreach(string name in new[]{"Primary","Secondary"}) {
            object usage=Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.WindowUsage",true),true);
            Set(usage,"UsedPercent",name=="Primary"?35d:71d);Set(usage,"ResetsAt",1792000000L);Set(snapshot,name,usage);
        }
        return snapshot;
    }
    private static byte[] Bytes(Bitmap bitmap) {
        BitmapData data=bitmap.LockBits(new Rectangle(Point.Empty,bitmap.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
        try {byte[] bytes=new byte[data.Stride*data.Height];Marshal.Copy(data.Scan0,bytes,0,bytes.Length);using(var hash=SHA256.Create())return hash.ComputeHash(bytes);}
        finally {bitmap.UnlockBits(data);}
    }
    private static void PixelTests(Assembly current,Assembly legacy) {
        Type oldSettings=legacy.GetType("CodexRateMonitorNative.MonitorSettings",true),oldRenderer=legacy.GetType("CodexRateMonitorNative.OverlayRenderer",true);
        object newSample=Sample(current),oldSample=Sample(legacy);
        object newCredits=Activator.CreateInstance(current.GetType("CodexRateMonitorNative.ResetCreditsInfo",true),true),
            oldCredits=Activator.CreateInstance(legacy.GetType("CodexRateMonitorNative.ResetCreditsInfo",true),true);
        Set(newCredits,"AvailableCount",3);Set(oldCredits,"AvailableCount",3);
        foreach(string language in new[]{"zh-CN","zh-TW","en"}) {
            current.GetType("CodexRateMonitorNative.I18n",true).GetMethod("SetLanguage").Invoke(null,new object[]{language});
            legacy.GetType("CodexRateMonitorNative.I18n",true).GetMethod("SetLanguage").Invoke(null,new object[]{language});
            foreach(double oldScale in new[]{0.75,0.85,1d,1.5}) foreach(string lines in new[]{"1","2"})
            foreach(bool credits in new[]{false,true}) foreach(int dpi in new[]{96,120,144,168,192,240}) {
                object old=Activator.CreateInstance(oldSettings,true),next=Parse("{\"Style\":{\"Scale\":"+oldScale.ToString("R",CultureInfo.InvariantCulture)+"}}");
                Set(Get(old,"Style"),"Scale",oldScale);Set(old,"Language",language);Set(next,"Language",language);
                Set(old,"DisplayLines",lines);Set(next,"DisplayLines",lines);Set(old,"ShowResetCredits",credits);Set(next,"ShowResetCredits",credits);
                using(Bitmap before=(Bitmap)oldRenderer.GetMethod("CreateBitmap").Invoke(null,new object[]{old,oldSample,credits?oldCredits:null,"",dpi}))
                using(Bitmap after=(Bitmap)Renderer.GetMethod("CreateBitmap").Invoke(null,new object[]{next,newSample,credits?newCredits:null,"",dpi})) {
                    Check(before.Size==after.Size && Convert.ToBase64String(Bytes(before))==Convert.ToBase64String(Bytes(after)),"old/new rendered pixels differ at "+oldScale+" / "+dpi+" / "+language);
                    pixelCases++;
                }
            }
        }
    }
}
'@ | Set-Content -LiteralPath $source -Encoding UTF8
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }
& $csc /nologo /target:exe /out:"$testExe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $source
if ($LASTEXITCODE -ne 0) { throw 'Scale migration regression compilation failed.' }
& $testExe $monitorExe $LegacyMonitorPath
if ($LASTEXITCODE -ne 0) { throw 'Scale migration regression failed.' }
