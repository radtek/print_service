﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Reflection;
using CommonEventSender;
using JobOrdersService;
using JobPropsService;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Linq;

namespace PrintWindowsService
{
    /// <summary>
    /// Class for the management of processing of input queue on printing of labels
    /// </summary>
    public sealed class PrintJobs : IDisposable
    {
        #region Const

        private const string cServiceTitle = "Сервис печати этикеток";
        /// <summary>
        /// The name of the system event source used by this service.
        /// </summary>
        private const string cSystemEventSourceName = "ArcelorMittal.PrintService.EventSource";

        /// <summary>
        /// The name of the system event log used by this service.
        /// </summary>
        private const string cSystemEventLogName = "AM.PrintService.ArcelorMittal.Log";

        /// <summary>
        /// The name of the configuration parameter for the print task frequency in seconds.
        /// </summary>
        private const string cPrintTaskFrequencyName = "PrintTaskFrequency";

        /// <summary>
        /// The name of the configuration parameter for the Odata service url.
        /// </summary>
        private const string cOdataService = "OdataServiceUri";

        ///// <summary>
        ///// The name of the configuration parameter for the Ghost Script path
        ///// </summary>
        //private const string cGhostScriptPath = "GhostScriptPath";

        /// <summary>
        /// The name of the configuration parameter for the SMTP host
        /// </summary>
        private const string cSMTPHost = "SMTPHost";

        /// <summary>
        /// The name of the configuration parameter for the SMTP port
        /// </summary>
        private const string cSMTPPort = "SMTPPort";

        #endregion

        #region Fields

        /// <summary>
        /// Time interval for checking print tasks
        /// </summary>
        private System.Timers.Timer printTimer;

        private PrintServiceProductInfo wmiProductInfo;
        private bool jobStarted = false;
        private string odataServiceUrl;

        /// <summary>	The event log. </summary>
        private EventLog eventLog;

        private Dictionary<string, Thread> printThreadConcurrentDictionary = new Dictionary<string, Thread>();

        private SimpleLogger simpleLogger = null; //new SimpleLogger();

        #endregion

        #region Property

        /// <summary>
        /// Gets the event log which is used by the service.
        /// </summary>
        public EventLog EventLog
        {
            get
            {
                lock (this)
                {
                    if (eventLog == null)
                    {
                        string lSystemEventLogName = cSystemEventLogName;
                        eventLog = new EventLog();
                        if (!System.Diagnostics.EventLog.SourceExists(cSystemEventSourceName))
                        {
                            System.Diagnostics.EventLog.CreateEventSource(cSystemEventSourceName, lSystemEventLogName);
                        }
                        else
                        {
                            lSystemEventLogName = EventLog.LogNameFromSourceName(cSystemEventSourceName, ".");
                        }
                        eventLog.Source = cSystemEventSourceName;
                        eventLog.Log = lSystemEventLogName;
                        //PrintLabelWS.eventLog = eventLog;

                        WindowsIdentity identity = WindowsIdentity.GetCurrent();
                        WindowsPrincipal principal = new WindowsPrincipal(identity);
                        if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                        {
                            eventLog.ModifyOverflowPolicy(OverflowAction.OverwriteAsNeeded, 7);
                        }
                    }
                    return eventLog;
                }
            }
        }

        /// <summary>
        /// Status of processing of queue
        /// </summary>
        public bool JobStarted
        {
            get
            {
                return jobStarted;
            }
        }

        #endregion

        #region Constructor

        /// <summary>	Default constructor. </summary>
        public PrintJobs()
        {
            // Set up a timer to trigger every print task frequency.
            int printTaskFrequencyInSeconds = int.Parse(System.Configuration.ConfigurationManager.AppSettings[cPrintTaskFrequencyName]);
            //dbConnectionString = System.Configuration.ConfigurationManager.ConnectionStrings[cConnectionStringName].ConnectionString;
            odataServiceUrl = System.Configuration.ConfigurationManager.AppSettings[cOdataService];
            SenderMonitorEvent.sendMonitorEvent(EventLog, string.Format("ODataServiceUrl = {0}", odataServiceUrl), EventLogEntryType.Information, 1);

            //PrintLabelWS.ghostScriptPath = System.Configuration.ConfigurationManager.AppSettings[cGhostScriptPath];
            PrintLabelWS.SMTPHost = System.Configuration.ConfigurationManager.AppSettings[cSMTPHost];
            PrintLabelWS.SMTPPort = int.Parse(System.Configuration.ConfigurationManager.AppSettings[cSMTPPort]);
            SenderMonitorEvent.sendMonitorEvent(EventLog, string.Format("SMTP config = {0}:{1}", PrintLabelWS.SMTPHost, PrintLabelWS.SMTPPort), EventLogEntryType.Information, 1);


            try
            {
                wmiProductInfo = new PrintServiceProductInfo(cServiceTitle,
                                                         Environment.MachineName,
                                                         Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                                                         DateTime.Now,
                                                         odataServiceUrl);
            }
#pragma warning disable CS0168 // The variable 'ex' is declared but never used
            catch (Exception ex)
#pragma warning restore CS0168 // The variable 'ex' is declared but never used
            {
                //SenderMonitorEvent.sendMonitorEvent(EventLog, string.Format("Failed to initialize WMI = {0}", ex.ToString()), EventLogEntryType.Error);
            }

            printTimer = new System.Timers.Timer();
            printTimer.Interval = printTaskFrequencyInSeconds * 1000; // seconds to milliseconds
            printTimer.Elapsed += new System.Timers.ElapsedEventHandler(OnPrintTimer);

            SenderMonitorEvent.sendMonitorEvent(EventLog, string.Format("Print Task Frequncy = {0}", printTaskFrequencyInSeconds), EventLogEntryType.Information, 1);
        }

        #endregion

        #region Destructor

        public void Dispose()
        {
            if (eventLog != null)
            {
                eventLog.Dispose();
            }

            if (printTimer != null)
            {
                printTimer.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Start of processing of input queue
        /// </summary>
        public void StartJob()
        {
            /*if (printLabel.lTemplate == null)
            {
                try
                {
                    printLabel.lTemplate = new LabelTemplate(printLabel.templateFile);
                }
                catch (Exception ex)
                {
                    string lLastError = "Error of Excel start: " + ex.ToString();
                    SenderMonitorEvent.sendMonitorEvent(vpEventLog, lLastError, EventLogEntryType.Error);
                    wmiProductInfo.LastServiceError = string.Format("{0}. On {1}", lLastError, DateTime.Now);
                    wmiProductInfo.PublishInfo();
                }
            }*/

            SenderMonitorEvent.sendMonitorEvent(EventLog, "Starting print service...", EventLogEntryType.Information, 1);

            printTimer.Start();

            SenderMonitorEvent.sendMonitorEvent(EventLog, "Print service has been started", EventLogEntryType.Information, 1);
            jobStarted = true;
        }

        /// <summary>
        /// Stop of processing of input queue
        /// </summary>
        public void StopJob()
        {
            SenderMonitorEvent.sendMonitorEvent(EventLog, "Stopping print service...", EventLogEntryType.Information, 2);

            //stop timers if working
            if (printTimer.Enabled)
                printTimer.Stop();

            SenderMonitorEvent.sendMonitorEvent(EventLog, "Print service has been stopped", EventLogEntryType.Information, 2);
            jobStarted = false;
        }

        /// <summary>
        /// Processing of input queue
        /// </summary>
        public void OnPrintTimer(object sender, System.Timers.ElapsedEventArgs args)
        {
            string lLastError = string.Empty;
            printTimer.Stop();
            SenderMonitorEvent.sendMonitorEvent(EventLog, "Monitoring the print activity", EventLogEntryType.Information, 3);

            try
            {
                RemoveAllNotAliveThreads();
                var printerAddresesToIgnore = printThreadConcurrentDictionary.Select(t => t.Key);

                LabeldbData lDbData = new LabeldbData(odataServiceUrl);
                JobOrders jobsToProcess = lDbData.getJobsToProcess();
                int CountJobsToProcess = jobsToProcess.JobOrdersObj.Count;
                SenderMonitorEvent.sendMonitorEvent(EventLog, "Jobs to process: " + CountJobsToProcess, EventLogEntryType.Information, 3);

                if (CountJobsToProcess > 0)
                {
                    var groupedJobsToProcess = jobsToProcess.JobOrdersObj.GroupBy(j => j.PrinterIP);
                    foreach (var jobVal in groupedJobsToProcess)
                    {
                        try
                        {
                            JobOrders.JobOrdersValue[] printerJobArray = jobVal.OrderBy(o => o.ID).ToArray();

                            if (string.IsNullOrEmpty(jobVal.Key))
                            {
                                PrintJobProps job = lDbData.getJobData(EventLog, printerJobArray.First());
                                throw new Exception(string.Format("Printer IP address missing for printer {0}.", job.PrinterNo));
                            }
                            else
                            {
                                if (!printThreadConcurrentDictionary.ContainsKey(jobVal.Key))
                                {
                                    Thread printThread = new Thread(DoPrintWork);
                                    printThreadConcurrentDictionary.Add(jobVal.Key, printThread);
                                    printThread.Start(printerJobArray);
                                    simpleLogger?.Info(string.Format("Thread with IP:{0} added to dictionary.", jobVal.Key));
                                }
                                else
                                    SenderMonitorEvent.sendMonitorEvent(EventLog, string.Format("Print Thread already exists, will be printed later, PrinterIP={0}.", jobVal.Key), EventLogEntryType.Information, 4);
                                //throw new Exception(string.Format("Print Thread can't be created for printer IP {0}.", jobVal.Key));
                            }
                        }
                        catch (Exception ex)
                        {
                            string details = GetWebExceptionDetails(ex);
                            lLastError = "Error: " + ex.ToString() + " Details: " + details;
                            SenderMonitorEvent.sendMonitorEvent(EventLog, lLastError, EventLogEntryType.Error, 4);
                            if (wmiProductInfo != null)
                                wmiProductInfo.LastServiceError = string.Format("{0}. On {1}", lLastError, DateTime.Now);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string details = GetWebExceptionDetails(ex);
                    lLastError = "Error getting jobs: " + ex.ToString() + " Details: " + details;
                    SenderMonitorEvent.sendMonitorEvent(EventLog, lLastError, EventLogEntryType.Error, 4);
                    if (wmiProductInfo != null)
                        wmiProductInfo.LastServiceError = string.Format("{0}. On {1}", lLastError, DateTime.Now);
                }
                catch (Exception exc)
                {
                    SenderMonitorEvent.sendMonitorEvent(EventLog, exc.Message, EventLogEntryType.Error, 4);
                }
            }
            finally
            {
                printTimer.Start();
            }
        }

        private string GetWebExceptionDetails(Exception ex)
        {
            string details = string.Empty;
            if (ex is System.Net.WebException)
            {
                var resp = new StreamReader((ex as System.Net.WebException).Response.GetResponseStream()).ReadToEnd();

                try
                {
                    dynamic obj = JsonConvert.DeserializeObject(resp);
                    details = obj.error.message;
                }
                catch
                {
                    details = resp;
                }
            }
            return details;
        }

        private void DoPrintWork(object data)
        {
            if (data is JobOrders.JobOrdersValue[])
            {
                int CountJobsToProcess = 0;
                string lLastError = string.Empty;

                try
                {
                    string lPrintState;
                    int lLastJobID = 0;
                    string lFactoryNumber = string.Empty;
                    LabeldbData lDbData = new LabeldbData(odataServiceUrl);
                    JobOrders.JobOrdersValue[] jobValues = data as JobOrders.JobOrdersValue[];
                    //CountJobsToProcess = jobValues.Length;

                    foreach (JobOrders.JobOrdersValue jobValue in jobValues)
                    {
                        CountJobsToProcess++;

                        try
                        {
                            PrintJobProps job = lDbData.getJobData(EventLog, jobValue);
                            lLastJobID = job.JobOrderID;
                            lFactoryNumber = job.getLabelParameter("FactoryNumber", "FactoryNumber");

                            if (job.isExistsTemplate)
                            {
                                if (string.IsNullOrEmpty(job.IpAddress))
                                    throw new Exception(string.Format("Printer IP address missing for printer {0}.", job.PrinterNo));

                                string randomFileName = job.IpAddress;//Path.GetRandomFileName().Replace(".", "");
                                //PrintLabelWS.ExcelTemplateFile = Path.GetTempPath() + "Label.xlsx";
                                //PrintLabelWS.PDFTemplateFile = Path.GetTempPath() + "Label.pdf";
                                //PrintLabelWS.BMPTemplateFile = Path.GetTempPath() + "Label.bmp";
                                PrintLabelWS printLabelWS = new PrintLabelWS()
                                {
                                    eventLog = EventLog,
                                    BMPTemplateFile = Path.GetTempPath() + randomFileName + ".bmp",
                                    ExcelTemplateFile = Path.GetTempPath() + randomFileName + ".xlsx",
                                    PDFTemplateFile = Path.GetTempPath() + randomFileName + ".pdf"
                                };

                                if (job.Command == "Print")
                                {
                                    string printerStatus = printLabelWS.getPrinterStatus(job.IpAddress, job.PrinterNo);
                                    Requests.updatePrinterStatus(odataServiceUrl, job.PrinterNo, printerStatus);
                                    if (!printerStatus.Equals("OK"))
                                    {
                                        throw new Exception(string.Format("Cannot print to {0}. Not valid printer status: {1}", job.PrinterNo, printerStatus));
                                    }
                                }
                                job.prepareTemplate(printLabelWS.ExcelTemplateFile);
                                if (job.Command == "Print")
                                {
                                    if (printLabelWS.PrintTemplate(job))
                                    {
                                        lPrintState = "Done";
                                        if (wmiProductInfo != null)
                                            wmiProductInfo.LastActivityTime = DateTime.Now;
                                    }
                                    else
                                    {
                                        lPrintState = "Failed";
                                    }
                                    lLastError = string.Format("JobOrderID: {0}. FactoryNumber: {3}. Print to: {1}. Status: {2}", job.JobOrderID, job.PrinterName, lPrintState, lFactoryNumber);
                                }
                                else
                                {
                                    if (printLabelWS.EmailTemplate(job))
                                    {
                                        lPrintState = "Done";
                                        if (wmiProductInfo != null)
                                            wmiProductInfo.LastActivityTime = DateTime.Now;
                                    }
                                    else
                                    {
                                        lPrintState = "Failed";
                                    }
                                    lLastError = string.Format("JobOrderID: {0}. FactoryNumber: {3}. Mail to: {1}. Status: {2}", job.JobOrderID, job.CommandRule, lPrintState, lFactoryNumber);
                                }
                                SenderMonitorEvent.sendMonitorEvent(EventLog, lLastError, lPrintState == "Failed" ? EventLogEntryType.Error : EventLogEntryType.Information, 4);
                                if (lPrintState == "Failed")
                                {
                                    if (wmiProductInfo != null)
                                        wmiProductInfo.LastServiceError = string.Format("{0}. On {1}", lLastError, DateTime.Now);
                                }

                                //Clear All PrintLabelWS Temp Files
                                if (printLabelWS.eventLog != null)
                                    printLabelWS.eventLog = null;
                                if (File.Exists(printLabelWS.BMPTemplateFile))
                                    File.Delete(printLabelWS.BMPTemplateFile);
                                if (File.Exists(printLabelWS.PDFTemplateFile))
                                    File.Delete(printLabelWS.PDFTemplateFile);
                                if (File.Exists(printLabelWS.ExcelTemplateFile))
                                    File.Delete(printLabelWS.ExcelTemplateFile);
                            }
                            else
                            {
                                lPrintState = "Failed";
                                lLastError = string.Format("Excel template is empty. JobOrderID: {0}. FactoryNumber: {1}.", job.JobOrderID, lFactoryNumber);
                                SenderMonitorEvent.sendMonitorEvent(EventLog, lLastError, EventLogEntryType.Error, 4);
                                if (wmiProductInfo != null)
                                    wmiProductInfo.LastServiceError = string.Format("{0}. On {1}", lLastError, DateTime.Now);
                            }

                            if (lPrintState == "Done")
                            {
                                Requests.updateJobStatus(odataServiceUrl, job.JobOrderID, lPrintState);
                            }
                            else if (lPrintState == "Failed")
                            {
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            string details = GetWebExceptionDetails(ex);
                            lLastError = "JobOrderID: " + lLastJobID + ". FactoryNumber: " + lFactoryNumber + " Error: " + ex.ToString() + " Details: " + details;
                            SenderMonitorEvent.sendMonitorEvent(EventLog, lLastError, EventLogEntryType.Error, 4);
                            if (wmiProductInfo != null)
                                wmiProductInfo.LastServiceError = string.Format("{0}. On {1}", lLastError, DateTime.Now);

                            break;
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (wmiProductInfo != null)
                        {
                            wmiProductInfo.PrintedLabelsCount += CountJobsToProcess;
                            wmiProductInfo.PublishInfo();
                        }
                        SenderMonitorEvent.sendMonitorEvent(EventLog, string.Format("Print is done. {0} tasks", CountJobsToProcess), EventLogEntryType.Information, 4);
                    }
                    catch (Exception exc)
                    {
                        SenderMonitorEvent.sendMonitorEvent(EventLog, exc.Message, EventLogEntryType.Error,4);
                    }
                }
            }
        }

        /// <summary>
        /// Removes all done printer Threads
        /// </summary>
        private void RemoveAllNotAliveThreads()
        {
            var lvThreadsToRemove = printThreadConcurrentDictionary.Where(t => t.Value.IsAlive == false).Select(t => t.Key).ToArray();
            foreach (var lvThreadToRemove in lvThreadsToRemove)
            {
                printThreadConcurrentDictionary.Remove(lvThreadToRemove);
                simpleLogger?.Info(string.Format("Thread with IP:{0} removed from dictionary.", lvThreadToRemove));

                //if (printThreadConcurrentDictionary.Remove(lvThreadToRemove))
                //    SenderMonitorEvent.sendMonitorEvent(EventLog, string.Format("Thread Printer IP:{0} Can't be removed.", lvThreadToRemove), EventLogEntryType.Warning, 4);
            }
        }

        #endregion
    }

    /// <summary>
    /// Class of label for print
    /// </summary>
    public class PrintJobProps : JobProps
    {
        private byte[] xlFile;
        private List<EquipmentPropertyValue> tableEquipmentProperty;
        private List<PrintPropertiesValue> tableLabelProperty;

        /// <summary>
        /// Printer name for print label
        /// </summary>
        public string PrinterName
        {
            get { return getEquipmentProperty("PRINTER_NAME"); }
        }

        /// <summary>
        /// IP of printer
        /// </summary>
        public string IpAddress
        {
            get { return getEquipmentProperty("PRINTER_IP"); }
        }

        /// <summary>
        /// Paper width in pixels
        /// </summary>
        public string PaperWidth
        {
            get { return getEquipmentProperty("PAPER_WIDTH"); }
        }

        /// <summary>
        /// Paper height in pixels
        /// </summary>
        public string PaperHeight
        {
            get { return getEquipmentProperty("PAPER_HEIGHT"); }
        }

        /// <summary>
        /// Printer NO
        /// </summary>
        public string PrinterNo
        {
            get { return getEquipmentProperty("PRINTER_NO"); }
        }

        /// <summary>
        /// Is exists template of label
        /// </summary>
        public bool isExistsTemplate
        {
            get { return (xlFile == null ? false : xlFile.Length > 0); }
        }

        /// <summary>	Constructor. </summary>
        ///
        /// <param name="jobOrderID">			  	Identifier for the job order. </param>
        /// <param name="command">				  	The command. </param>
        /// <param name="commandRule">			  	The command rule. </param>
        /// <param name="xlFile">				  	The xl file. </param>
        /// <param name="tableEquipmentProperty">	The table equipment property. </param>
        /// <param name="tableLabelProperty">	  	The table label property. </param>
        public PrintJobProps(int jobOrderID,
                             string command,
                             string commandRule,
                             byte[] xlFile,
                             List<EquipmentPropertyValue> tableEquipmentProperty,
                             List<PrintPropertiesValue> tableLabelProperty) : base(jobOrderID,
                                                                                   command,
                                                                                   commandRule)
        {
            this.xlFile = xlFile;
            this.tableEquipmentProperty = tableEquipmentProperty;
            this.tableLabelProperty = tableLabelProperty;
        }
        /// <summary>
        /// Prepare template for print
        /// </summary>
        public void prepareTemplate(string excelTemplateFile)
        {
            if (isExistsTemplate)
            {
                using (FileStream fs = new FileStream(excelTemplateFile, FileMode.Create))
                {
                    fs.Write(xlFile, 0, xlFile.Length);
                    //fs.Close();
                }
            }
        }
        /// <summary>
        /// Return label parameter value by TypeProperty and PropertyCode
        /// </summary>
        public string getLabelParameter(string typeProperty, string propertyCode)
        {
            string result = string.Empty;
            if (tableLabelProperty != null)
            {
                PrintPropertiesValue propertyFind = tableLabelProperty.Find(x => (x.TypeProperty == typeProperty) & (x.PropertyCode == propertyCode));
                if (propertyFind != null)
                {
                    result = propertyFind.Value;
                }
            }

            return result;
        }
        /// <summary>
        /// Return equipment property value by Property
        /// </summary>
        public string getEquipmentProperty(string property)
        {
            string result = string.Empty;
            if (tableEquipmentProperty != null)
            {
                EquipmentPropertyValue propertyFind = tableEquipmentProperty.Find(x => (x.Property == property));
                if (propertyFind != null)
                {
                    result = propertyFind.Value == null ? string.Empty : propertyFind.Value.ToString();
                }
            }

            return result;
        }
    }
}
