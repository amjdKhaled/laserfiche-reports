using System;
using System.Threading;
using System.Windows.Forms;
using WixToolset.Mba.Core;

[assembly: BootstrapperApplicationFactory(typeof(LaserficheReports.Setup.BaFactory))]
namespace LaserficheReports.Setup
{
    public sealed class BaFactory : BaseBootstrapperApplicationFactory
    {
        protected override IBootstrapperApplication Create(IEngine engine, IBootstrapperCommand command) => new ReportsBa(engine, command);
    }
    public sealed class ReportsBa : BootstrapperApplication
    {
        private readonly IEngine engine;
        private readonly IBootstrapperCommand command;
        private Wizard? form;
        private readonly ManualResetEvent done = new ManualResetEvent(false);
        private int exitCode;
        private bool silent;
        internal bool Installed { get; private set; }
        internal bool Detected { get; private set; }
        internal event Action? DetectedChanged;
        internal event Action<int>? ProgressChanged;
        internal event Action<bool, string>? Completed;
        public ReportsBa(IEngine engine, IBootstrapperCommand command) : base(engine) { this.engine = engine; this.command = command; }
        protected override void Run()
        {
            // Related bundles launched by Burn during upgrade must never show a second wizard.
            silent = command.Relation != RelationType.None || command.Display == Display.Embedded || command.Display == Display.None;
            if (silent) { engine.Detect(); done.WaitOne(); engine.Quit(exitCode); return; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            form = new Wizard(this, command.Action);
            form.Shown += (_, __) => engine.Detect(form.Handle);
            Application.Run(form); engine.Quit(exitCode);
        }
        internal void Begin(LaunchAction action, string profile)
        {
            engine.SetVariableString("ReportsProfile", profile, false);
            engine.Plan(action);
        }
        private void Ui(Action action) { if (form != null && !form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(action); }
        protected override void OnDetectPackageComplete(DetectPackageCompleteEventArgs e)
        { if (e.PackageId == "ReportsMsi") Installed = e.State == PackageState.Present; base.OnDetectPackageComplete(e); }
        protected override void OnDetectComplete(DetectCompleteEventArgs e)
        {
            Detected = e.Status >= 0;
            if (silent) { if (Detected) engine.Plan(command.Action); else { exitCode = e.Status; done.Set(); } }
            else if (Detected) Ui(() => DetectedChanged?.Invoke());
            else Ui(() => Completed?.Invoke(false, $"Environment detection failed: 0x{e.Status:X8}"));
        }
        protected override void OnPlanComplete(PlanCompleteEventArgs e)
        { if (e.Status >= 0) engine.Apply(form?.Handle ?? IntPtr.Zero); else Finish(e.Status); }
        protected override void OnApplyComplete(ApplyCompleteEventArgs e) => Finish(e.Status);
        protected override void OnExecuteProgress(ExecuteProgressEventArgs e) => Ui(() => ProgressChanged?.Invoke(e.OverallPercentage));
        protected override void OnError(WixToolset.Mba.Core.ErrorEventArgs e)
        { engine.Log(LogLevel.Error, e.ErrorMessage); e.Result = Result.Ok; }
        private void Finish(int status)
        {
            exitCode = status >= 0 ? 0 : status;
            if (silent) done.Set();
            else Ui(() => Completed?.Invoke(status >= 0, status >= 0 ? "Installation transaction completed." : $"Setup failed: 0x{status:X8}. See the Burn/MSI log."));
        }
    }
}
