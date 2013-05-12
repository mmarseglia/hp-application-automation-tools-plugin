﻿//© Copyright 2013 Hewlett-Packard Development Company, L.P.
//Permission is hereby granted, free of charge, to any person obtaining a copy of this software
//and associated documentation files (the "Software"), to deal in the Software without restriction,
//including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
//and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
//subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all copies or
//substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
//INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
//PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE 
//LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, 
//TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE 
//OR OTHER DEALINGS IN THE SOFTWARE.



using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HP.LoadRunner.Interop.Wlrun;
using HpToolsLauncher.Properties;
using System.Xml;
//using Analysis.Api;

namespace HpToolsLauncher.TestRunners
{


    public class PerformanceTestRunner : IFileSysTestRunner
    {
        public const string LRR_FOLDER = "LRR";
        public const string LRA_FOLDER = "LRA";
        public const string HTML_FOLDER = "HTML";
        public const string ANALYSIS_LAUNCHER = @".\LRAnalysisLauncher.exe";

        private IAssetRunner _runner;
        private TimeSpan _timeout;
        private int _pollingInterval;
        private TimeSpan _perScenarioTimeOut;
        private RunCancelledDelegate _runCancelled;

        private bool _scenarioEnded;
        private bool _scenarioEndedEvent;
        private LrEngine _engine;
        private Stopwatch _stopWatch;
        private string _resultsFolder;
        private List<string> _ignoreErrorStrings;

        private enum VuserStatus
        {
            Down = 1,
            Pending = 2,
            Init = 3,
            Ready = 4,
            Run = 5,
            Rendez = 6,
            Passed = 7,
            Failed = 8,
            Error = 9,
            GradualExiting = 12,
            Exiting = 10,
            Stopped = 11
        };
        private int[] _vuserStatus = new int[13];

        private enum ERRORState { Ignore, Error };

        private class ControllerError
        {
            public ERRORState state { get; set; }
            public int occurences { get; set; }
        };

        Dictionary<string, ControllerError> _errors;
        int _errorsCount;




        public PerformanceTestRunner(IAssetRunner runner, TimeSpan timeout, int pollingInterval, TimeSpan perScenarioTimeOut, List<string> ignoreErrorStrings)
        {
            this._runner = runner;
            this._timeout = timeout;
            this._pollingInterval = pollingInterval;
            this._perScenarioTimeOut = perScenarioTimeOut;
            this._ignoreErrorStrings = ignoreErrorStrings;
            this._scenarioEnded = false;
            _engine = null;
            this._errors = null;
            this._errorsCount = 0;
        }

        public TestRunResults RunTest(string scenarioPath, ref string errorReason, RunCancelledDelegate runCancelled)
        {

            //prepare the instance that will contain test results for JUnit
            TestRunResults runDesc = new TestRunResults();

            ConsoleWriter.ActiveTestRun = runDesc;
            ConsoleWriter.WriteLine(DateTime.Now.ToString(Launcher.DateFormat) + " Running: " + scenarioPath);

            runDesc.TestType = TestType.LoadRunner.ToString();
            _resultsFolder = Helper.GetTempDir();

            //a directory with this name may already exist. try to delete it.
            if (Directory.Exists(_resultsFolder))
            {
                try
                {
                    // Directory.Delete(_resultsFolder, true);
                    DirectoryInfo dir = new DirectoryInfo(_resultsFolder);
                    dir.GetFiles().ToList().ForEach(file => file.Delete());
                    dir.GetDirectories().ToList().ForEach(subdir => subdir.Delete());
                }
                catch (Exception)
                {
                    Console.WriteLine(string.Format(Resources.CannotDeleteReportFolder, _resultsFolder));
                }

            }
            else
            {
                try
                {
                    Directory.CreateDirectory(_resultsFolder);
                }
                catch (Exception e)
                {
                    errorReason = string.Format(Resources.FailedToCreateTempDirError, _resultsFolder);
                    runDesc.TestState = TestState.Error;
                    runDesc.ErrorDesc = errorReason;

                    Environment.ExitCode = (int)Launcher.ExitCodeEnum.Failed;
                    return runDesc;
                }
            }
            //create LRR folder:
            Directory.CreateDirectory(Path.Combine(_resultsFolder, LRR_FOLDER));

            //init result params
            runDesc.ErrorDesc = errorReason;
            runDesc.TestPath = scenarioPath;
            runDesc.TestState = TestState.Unknown;

            if (!Helper.isLoadRunnerInstalled())
            {
                runDesc.TestState = TestState.Error;
                runDesc.ErrorDesc = string.Format(Resources.LoadRunnerNotInstalled, System.Environment.MachineName);
                ConsoleWriter.WriteErrLine(runDesc.ErrorDesc);
                Environment.ExitCode = (int)Launcher.ExitCodeEnum.Failed;
                return runDesc;
            }

            //from here on, we may delegate runCancelled().
            _runCancelled = runCancelled;

            //start scenario stop watch
            Stopwatch scenarioStopWatch = Stopwatch.StartNew();

            //set state to running
            runDesc.TestState = TestState.Running;

            //and run the scenario
            bool res = runScenario(scenarioPath, ref errorReason, runCancelled);

            if (!res)
            {
                //runScenario failed. print the error and set test as failed
                ConsoleWriter.WriteErrLine(errorReason);
                runDesc.TestState = TestState.Error;
                runDesc.ErrorDesc = errorReason;
                runDesc.Runtime = scenarioStopWatch.Elapsed;

                //and try to close the controller
                closeController();
                return runDesc;
            }
            else
            {
                try
                {
                    ConsoleWriter.WriteLine(Resources.GeneralDoubleSeperator);
                    runDesc.ReportLocation = _resultsFolder;
                    ConsoleWriter.WriteLine(Resources.LrAnalysingResults);

                    //close the controller, so Analysis can be opened
                    closeController();

                    //generate report using Analysis:
                    generateAnalysisReport(runDesc);



                    //check for errors:
                    if (File.Exists(Path.Combine(_resultsFolder, "errors.xml")))
                    {
                        checkForErrors();
                    }

                    ConsoleWriter.WriteLine(Resources.LRErrorsSummary);

                    //count how many ignorable errors and how many fatal errors occured.
                    int ignore = getErrorsCount(ERRORState.Ignore);
                    int fatal = getErrorsCount(ERRORState.Error);
                    ConsoleWriter.WriteLine(String.Format(Resources.LrErrorSummeryNum, ignore, fatal));
                    ConsoleWriter.WriteLine("");
                    if (_errors != null && _errors.Count > 0)
                    {
                        foreach (ERRORState state in Enum.GetValues(typeof(ERRORState)))
                        {
                            ConsoleWriter.WriteLine(printErrorSummary(state));
                        }
                    }

                    //if scenario ended with fatal errors, change test state
                    if (fatal > 0)
                    {
                        ConsoleWriter.WriteErrLine(string.Format(Resources.LRTestFailDueToFatalErrors, fatal));
                        errorReason = buildErrorReasonForErrors();
                        runDesc.TestState = TestState.Failed;
                    }
                    else if (ignore > 0)
                    {
                        ConsoleWriter.WriteLine(string.Format(Resources.LRTestWarningDueToIgnoredErrors, ignore));
                        runDesc.HasWarnings = true;
                        runDesc.TestState = TestState.Warning;
                    }
                    else
                    {
                        Console.WriteLine(Resources.LRTestPassed);
                        runDesc.TestState = TestState.Passed;
                    }






                }
                catch (Exception e)
                {
                    ConsoleWriter.WriteException(Resources.LRExceptionInAnalysisRunner, e);
                    runDesc.TestState = TestState.Error;
                    runDesc.ErrorDesc = Resources.LRExceptionInAnalysisRunner;
                    runDesc.Runtime = scenarioStopWatch.Elapsed;
                }

                //runDesc.ReportLocation = _resultsFolder;

            }

            runDesc.Runtime = scenarioStopWatch.Elapsed;
            if (!string.IsNullOrEmpty(errorReason))
                runDesc.ErrorDesc = errorReason;
            closeController();
            return runDesc;
        }

        private string buildErrorReasonForErrors()
        {
            //ConsoleWriter.WriteLine("building Error ResultString");
            string res = Resources.LRErrorReasonSummaryTitle;
            res += printErrorSummary(ERRORState.Error);
            return res;
        }

        private string printErrorSummary(ERRORState state)
        {
            string res = "";
            List<string> validKeys = (from x in _errors where x.Value.state == state select x.Key).ToList<string>();
            if (validKeys.Count == 0)
            {
                return "";
            }

            res += state + " summary:\n";
            foreach (string errorString in validKeys)
            {
                res += (_errors[errorString].occurences + " : " + errorString) + "\n";
            }
            return res;
        }

        private bool runScenario(string scenario, ref string errorReason, RunCancelledDelegate runCancelled)
        {
            cleanENV();

            ConsoleWriter.WriteLine(string.Format(Resources.LrInitScenario, scenario));

            //start controller
            _engine = new LrEngine();
            if (_engine == null)
            {
                errorReason = string.Format(Resources.LrFailToOpenController, scenario);
                return false;
            }

            //try to register the end scenario event:
            _scenarioEndedEvent = false;
            try
            {
                _engine.Events.ScenarioEvents.OnScenarioEnded += ScenarioEvents_OnScenarioEnded;
                _scenarioEndedEvent = true;
            }
            catch (Exception e)
            {
                ConsoleWriter.WriteException(Resources.LrFailToRegisterEndScenarioEvent, e);
                _scenarioEndedEvent = false;
            }

            _engine.ShowMainWindow(0);
#if DEBUG
            _engine.ShowMainWindow(1);
#endif
            //pointer to the scenario object:
            LrScenario currentScenario = _engine.Scenario;

            //try to open the scenario and validate the scenario and connect to load generators
            if (openScenario(scenario, ref errorReason) && validateScenario(currentScenario, ref errorReason))
            {
                //set the result dir:
                currentScenario.ResultDir = Path.Combine(_resultsFolder, LRR_FOLDER);

                //check if canceled or timedOut:
                if (_runCancelled())
                {
                    errorReason = Resources.GeneralTimedOut;
                    return false;
                }

                _scenarioEnded = false;

                ConsoleWriter.WriteLine(Resources.LrStartScenario);

                int ret = currentScenario.Start();

                if (ret != 0)
                {
                    errorReason = string.Format(Resources.LrStartScenarioFail, scenario, ret);
                    return false;
                }
                //per scenario timeout stopwatch
                _stopWatch = Stopwatch.StartNew();

                //wait for scenario to end:
                if (!waitForScenario(ref errorReason))
                {
                    //something went wrong during scenario execution, error reason set in errorReason string
                    return false;
                }
                else
                {//scenario has ended
                    Console.WriteLine(string.Format(Resources.LrScenarioEnded, scenario));

                    //collate results
                    collateResults();
                }
            }
            else
            {
                return false;
            }

            return true;
        }

        private bool openScenario(string scenario, ref string errorReason)
        {
            int ret;
            try
            {
                ThreadStart tstart = () =>
                {
                    try
                    {
                        ret = _engine.Scenario.Open(scenario, false);
                        if (ret != 0)
                        {
                            throw new Exception(ret.ToString());
                        }
                    }
                    catch (Exception) { }
                };
                Thread t = new Thread(tstart);
                t.Start();
                if (!t.Join(_pollingInterval * 1000))
                {
                    errorReason = "cannot open scenario - timeout!";
                    return false;
                }
            }
            catch (Exception e)
            {
                errorReason = string.Format(Resources.LrFailedToOpenScenario, scenario, int.Parse(e.Message));
                return false;
            }
            return true;
        }



        private void generateAnalysisReport(TestRunResults runDesc)
        {
            string lrrLocation = Path.Combine(runDesc.ReportLocation, LRR_FOLDER, LRR_FOLDER + ".lrr");
            string lraLocation = Path.Combine(runDesc.ReportLocation, LRA_FOLDER, LRA_FOLDER + ".lra");
            string htmlLocation = Path.Combine(runDesc.ReportLocation, HTML_FOLDER, HTML_FOLDER + ".html");

            ProcessStartInfo analysisRunner = new ProcessStartInfo();
            analysisRunner.FileName = ANALYSIS_LAUNCHER;
            analysisRunner.Arguments = lrrLocation + " " + lraLocation + " " + htmlLocation;
            analysisRunner.UseShellExecute = false;
            analysisRunner.RedirectStandardOutput = true;

            Process runner = Process.Start(analysisRunner);
            if (runner != null)
            {
                Stopwatch analysisStopWatch = Stopwatch.StartNew();

                while (!runner.WaitForExit(_pollingInterval * 1000) && analysisStopWatch.Elapsed < _perScenarioTimeOut) ;

                analysisStopWatch.Stop();
                if (analysisStopWatch.Elapsed > _perScenarioTimeOut)
                {
                    runDesc.ErrorDesc = Resources.LrAnalysisTimeOut;
                    ConsoleWriter.WriteErrLine(runDesc.ErrorDesc);
                    runDesc.TestState = TestState.Error;
                    if (!runner.HasExited)
                    {
                        runner.Kill();
                    }
                }
                //ConsoleWriter.WriteLine("checking error code");
                if (runner.ExitCode != (int)Launcher.ExitCodeEnum.Passed)
                {
                    runDesc.ErrorDesc = Resources.LrAnalysisRunTimeError;
                    ConsoleWriter.WriteErrLine(runDesc.ErrorDesc);
                    runDesc.TestState = TestState.Error;
                }
                using (StreamReader reader = runner.StandardOutput)
                {
                    string result = reader.ReadToEnd();
                    ConsoleWriter.WriteLine(Resources.LrAnlysisResults);
                    ConsoleWriter.WriteLine("");
                    ConsoleWriter.WriteLine(result);
                }

            }
            else
            {
                runDesc.ErrorDesc = Resources.LrAnlysisInitFail;
                ConsoleWriter.WriteErrLine(runDesc.ErrorDesc);
                runDesc.TestState = TestState.Error;
            }

        }


        private void collateResults()
        {
            int ret = _engine.Scenario.CollateResults();
            if (ret != 0)
            {
                ConsoleWriter.WriteErrLine(string.Format(Resources.LrScenarioCollateFail, ret));
            }

            while (!_engine.Scenario.IsResultsCollated() && _stopWatch.Elapsed < _perScenarioTimeOut)
            {
                Thread.Sleep(_pollingInterval * 1000);
            }
        }

        private void closeController()
        {
            //try to gracefully shut down the controller
            if (_engine != null)
            {
                int rc = _engine.CloseController();
                if (rc != 0)
                {
                    ConsoleWriter.WriteErrLine("\t\tFailed to close Controller with CloseController API function, rc: " + rc);
                }

                //give the controller 15 secs to shutdown. otherwise, print an error.
                Thread.Sleep(15000);

                var process = Process.GetProcessesByName("Wlrun");
                if (process.Length > 0)
                {
                    ConsoleWriter.WriteErrLine("\t\tThe Controller is still running...");
                    return;
                }
            }
            _engine = null;
        }

        private bool waitForScenario(ref string errorReason)
        {

            //wait for the scenario to end gracefully:
            while (!_scenarioEnded)
            {
                //ConsoleWriter.WriteLine("waitForScenario");
                if (_runCancelled())
                {
                    errorReason = Resources.GeneralTimedOut;
                    return false;
                }

                Thread.Sleep(_pollingInterval * 1000);


                if (_stopWatch.Elapsed > _perScenarioTimeOut)
                {
                    _stopWatch.Stop();
                    ConsoleWriter.WriteErrLine(string.Format(Resources.LrScenarioTimeOut, _stopWatch.Elapsed.Minutes));
                    errorReason = string.Format(Resources.LrScenarioTimeOut, _stopWatch.Elapsed.Minutes);
                    break;
                }

                //checkForErrors();


                //if (getErrorsCount(ERRORState.Error) > 0)
                //{
                //    //need to stop the scenario, there is an un ignorable error:
                //    break;
                //}

                // if the end scenario event was not registered, 
                if (!_scenarioEndedEvent)
                {

                    //if all Vusers are in ending state, scenario is finished.
                    _scenarioEnded = isFinished();
                }
            }

            if (_scenarioEndedEvent)
            {
                try
                {
                    //ConsoleWriter.WriteLine("unregistering event");
                    _engine.Events.ScenarioEvents.OnScenarioEnded -= ScenarioEvents_OnScenarioEnded;
                    _scenarioEndedEvent = false;
                }
                catch { }
            }

            //if scenario not ended until now, force stop it.
            if (!_scenarioEnded)
            {

                ConsoleWriter.WriteErrLine(Resources.LrScenarioTimeOut);

                int ret = _engine.Scenario.StopNow();
                if (ret != 0)
                {
                    errorReason = string.Format(Resources.LrStopScenarioEnded);
                    return false;
                }

                int tries = 2;
                while (_engine.Scenario.IsActive() && tries > 0)
                {
                    //ConsoleWriter.WriteLine("\t\tScenario is still running. Waiting for the scenario to stop...");
                    Thread.Sleep(_pollingInterval * 1000);
                    tries--;
                }

                if (_engine.Scenario.IsActive())
                {
                    errorReason = Resources.LrControllerFailedToStop;
                    return false;
                }
            }

            return true;
        }

        private void checkForErrors()
        {
            //init variables
            string message = null;

            XmlDocument errorsXML = new XmlDocument();
            errorsXML.Load(Path.Combine(_resultsFolder, "errors.xml"));

            _errors = new Dictionary<string, ControllerError>();

            //new unseen error(s)
            foreach (XmlNode errorNode in errorsXML.DocumentElement.ChildNodes)
            {
                message = errorNode.InnerText;

                ControllerError cerror;
                //if error exist, just update the count:
                bool added = false;
                //check if the error is ignorable
                foreach (string ignoreError in _ignoreErrorStrings)
                {
                    if (message.ToLowerInvariant().Contains(ignoreError.ToLowerInvariant()))
                    {
                        ConsoleWriter.WriteLine(string.Format(Resources.LrErrorIgnored, message, ignoreError));
                        _errors.Add(message, new ControllerError { state = ERRORState.Ignore, occurences = 1 });
                        added = true;
                        _errorsCount++;
                    }
                }

                //error was not ignored, and was not added yet. add it now.
                if (!added)
                {
                    // non ignorable error message,
                    ConsoleWriter.WriteErrLine(message);//+ ", time " + time + ", host: " + host + ", VuserID: " + vuserId + ", script: " + script + ", line: " + line);
                    _errors.Add(message, new ControllerError { state = ERRORState.Error, occurences = 1 });
                    _errorsCount++;
                    //if the scenario ended event was not registered, we need to provide the opportunity to check the vuser status.
                    //if (!_scenarioEndedEvent)
                    //{
                    break;
                    //}
                }

            }



        }


        private int getErrorsCount(ERRORState state)
        {
            return (_errors != null) ? (from x in _errors where x.Value.state == state select x.Value.occurences).Sum() : 0;
        }



        private void updateError(string message)
        {

            ControllerError s = _errors[message];
            if (s != null)
            {
                s.occurences++;
                _errors[message] = s;
            }

        }

        private void ScenarioEvents_OnScenarioEnded()
        {
            //ConsoleWriter.WriteLine("scenario ended event");
            _scenarioEnded = true;

        }


        private bool isFinished()
        {
            updateVuserStatus();
            bool isFinished = false;

            isFinished = _vuserStatus[(int)VuserStatus.Down] == 0 &&
                         _vuserStatus[(int)VuserStatus.Pending] == 0 &&
                         _vuserStatus[(int)VuserStatus.Init] == 0 &&
                         _vuserStatus[(int)VuserStatus.Ready] == 0 &&
                         _vuserStatus[(int)VuserStatus.Run] == 0 &&
                         _vuserStatus[(int)VuserStatus.Rendez] == 0 &&
                         _vuserStatus[(int)VuserStatus.Exiting] == 0 &&
                         _vuserStatus[(int)VuserStatus.GradualExiting] == 0;

            return isFinished;
        }

        private void printVusersStatus()
        {
            Console.WriteLine("Vusers status:");
            string res = "";
            foreach (var val in Enum.GetValues(typeof(VuserStatus)))
            {
                res += ((VuserStatus)val) + ": " + _vuserStatus[(int)val] + " , ";
            }
            res = res.Substring(0, res.LastIndexOf(","));
            ConsoleWriter.WriteLine(res);
        }

        private void updateVuserStatus()
        {
            foreach (int val in Enum.GetValues(typeof(VuserStatus)))
            {
                _vuserStatus[val] = _engine.Scenario.GetVusersCount(val);
            }
        }

        private bool validateScenario(LrScenario scenario, ref string errorReason)
        {

            //validate that scenario has SLA
            if (!scenario.DoesScenarioHaveSLAConfiguration())
            {
                errorReason = string.Format(Resources.LrScenarioValidationFailNoSLA, scenario.FileName);
                return false;

            }
            //validate that all scripts are available.
            if (!scenario.AreScriptsAccessible(out errorReason))
            {
                //error message in errorReason
                return false;
            }

            //validate that scenario has time limited schedule:
            if (!scenario.DoesScenarioHaveLimitedSchedule(out errorReason))
            {
                //error message in errorReason
                return false;
            }

            //validate LGs:
            if (scenario.Hosts.Count == 0)
            {
                errorReason = string.Format(Resources.LrScenarioValidationFailNoLGs, scenario.FileName);
                return false;
            }

            //connect to all active load generators == hosts:
            int ret;
            foreach (LrHost lg in scenario.Hosts)
            {
                //handle only active hosts
                if (lg.IsUsed())
                {
                    //connect to the host
                    ret = lg.Connect();
                    if (ret != 0)
                    {
                        errorReason = string.Format(Resources.LrScenarioValidationCannotConnectLG, lg.Name);
                        return false;
                    }

                    //sync with the host
                    ret = lg.Sync(60);
                    if (ret <= 0)
                    {
                        errorReason = string.Format(Resources.LrScenarioValidationCannotSyncLG, lg.Name); ;
                        return false;
                    }

                    //if host is not ready after sync, invalidate the test
                    if (lg.Status != LrHostStatus.lrHostReady)
                    {
                        errorReason = string.Format(Resources.LrScenarioValidationLGNotReady, lg.Name);
                        return false;
                    }

                    //if we got this far, lg is connected and ready to go
                    ConsoleWriter.WriteLine(string.Format(Resources.LrScenarioValidationLGConnected, lg.Name));
                }
            }
            return true;
        }

        private void cleanENV()
        {
            ConsoleWriter.WriteLine(Resources.LrCleanENV);
            try
            {
                // check if any mdrv.exe process existed, kill them.
                var mdrvProcesses = Process.GetProcessesByName("mdrv");
                foreach (Process p in mdrvProcesses)
                {
                    p.Kill();
                    Thread.Sleep(500);

                }

                // check if any wlrun.exe process existed, kill them.

                var wlrunProcesses = Process.GetProcessesByName("Wlrun");
                if (wlrunProcesses.Length > 0)
                {
                    foreach (Process p in wlrunProcesses)
                    {
                        p.Kill();
                        // When kill wlrun process directly, there might be a werfault.exe process generated, kill it if it appears.
                        DateTime nowTime = DateTime.Now;
                        while (DateTime.Now.Subtract(nowTime).TotalSeconds < 10)
                        {
                            var werFaultProcesses = Process.GetProcessesByName("WerFault");
                            if (werFaultProcesses.Length > 0)
                            {
                                //Console.WriteLine("Kill process of WerFault");
                                foreach (Process pf in werFaultProcesses)
                                {
                                    pf.Kill();
                                }
                                break;
                            }
                            Thread.Sleep(1000);
                        }
                        Thread.Sleep(1000);
                    }
                    ConsoleWriter.WriteLine("wlrun killed");
                }
            }
            catch (Exception e)
            {

            }
        }

        public void CleanUp()
        {
            //ConsoleWriter.WriteLine("clenUp");

            //ConsoleWriter.WriteLine("Closing controller");
            closeController();
            cleanENV();
        }

    }
}