﻿using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using System.Diagnostics;
using System.Threading;
using System.Data.SqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CloudRoboticsUtil;

namespace CloudRoboticsFX.Worker
{
    class RoboticsEventProcessor : IEventProcessor
    {
        // Lock Object for multi-thread
        private static string thisLock = "{ThisObjectLock1}";
        private static string thisLock2 = "{ThisObjectLock2}";

        private ApplicationException ae = null;

        // Cloud to Device Message Logging
        public static bool rbC2dLogEnabled = false;
        public static string rbC2dLogEventHubConnString = string.Empty;
        public static string rbC2dLogEventHubName = string.Empty;

        // Error & Info message trace
        public static string rbTraceStorageConnString = string.Empty;
        public static string rbTraceTableName = string.Empty;
        public static string rbTraceLevel = string.Empty;
        public static string rbIotHubConnString = string.Empty;
        
        // SQL Database Info for Cloud Robotics FX 
        public static string rbSqlConnectionString = string.Empty;
        public static string rbEncPassPhrase = string.Empty;
        public static int rbCacheExpiredTimeSec = 60;

        public static string archivedDirectoryName = string.Empty;

        // SQL Database Table Cache
        public static Dictionary<string, object> rbCustomerRescCacheDic = new Dictionary<string, object>();
        public static Dictionary<string, object> rbAppMasterCacheDic = new Dictionary<string, object>();
        public static Dictionary<string, object> rbAppRouterCacheDic = new Dictionary<string, object>();
        public static Dictionary<string, object> rbAppDllCacheInfoDic = new Dictionary<string, object>();

        // App Domain for dll load
        private static string appDomanNameBase = "AppDomain_P";
        public static Dictionary<string, AppDomain> appDomainList = new Dictionary<string, AppDomain>();

        Task IEventProcessor.OpenAsync(PartitionContext context)
        {
            RbTraceLog.Initialize(rbTraceStorageConnString, rbTraceTableName, "CloudRoboticsFX");
            string msg = string.Format("RoboticsEventProcessor initialize.  Partition:{0}, Offset:{1}",
                         context.Lease.PartitionId, context.Lease.Offset);
            RbTraceLog.WriteLog(msg);

            return Task.FromResult<object>(null);
        }

        async Task IEventProcessor.ProcessEventsAsync(PartitionContext context, IEnumerable<EventData> messages)
        {
            int messagecnt = 0;
            bool sqlex_on = false;

            foreach (EventData eventData in messages)
            {
                ++messagecnt;
                bool devRouting = false;
                bool appRouting = false;
                bool rbHeaderNotFound = false;
                string sqlConnString = rbSqlConnectionString;
                string iothub_deviceId = (string)eventData.SystemProperties["iothub-connection-device-id"];
                DateTime iothub_enqueuedTimeUtc = (DateTime)eventData.SystemProperties["EnqueuedTimeUtc"];
                string text_message = Encoding.UTF8.GetString(eventData.GetBytes());
                string text_message_100 = text_message.Substring(0, 100);
                if (text_message_100.IndexOf(RbFormatType.RbHeader) < 0)
                {
                    rbHeaderNotFound = true;
                    RbTraceLog.WriteLog(string.Format(RbExceptionMessage.RbHeaderNotFound + "  Partition:{0}, Message:{1}",
                             context.Lease.PartitionId, text_message_100));
                }

                if (rbTraceLevel == RbTraceType.Detail)
                {
                    RbTraceLog.WriteLog(string.Format("RoboticsEventProcessor Message received.  Partition:{0}, Message:{1}",
                                 context.Lease.PartitionId, text_message));
                }
                JObject jo_message = null;

                if (!rbHeaderNotFound)  // Skip invalid data
                {
                    try
                    {
                        jo_message = JsonConvert.DeserializeObject<JObject>(text_message);

                        // Check RbHeader simplly
                        var jo_rbh = (JObject)jo_message[RbFormatType.RbHeader];
                        if (jo_rbh != null)
                        {
                            string jo_rbh_RoutingType = (string)jo_rbh[RbHeaderElement.RoutingType];
                            if (jo_rbh_RoutingType == RbRoutingType.LOG)
                            {
                                continue;
                            }
                        }

                        // Check RbHeader in detail
                        RbHeaderBuilder hdBuilder = new RbHeaderBuilder(jo_message, iothub_deviceId);
                        RbHeader rbh = hdBuilder.ValidateJsonSchema();

                        // Check Routing
                        if (rbh.RoutingType == RbRoutingType.CALL)
                        {
                            appRouting = true;
                        }
                        else if (rbh.RoutingType == RbRoutingType.D2D)
                        {
                            devRouting = true;
                            if (rbh.AppProcessingId != string.Empty)
                            {
                                appRouting = true;
                            }
                        }
                        else if (rbh.RoutingType == RbRoutingType.CONTROL)
                        {
                            devRouting = false;
                            appRouting = false;
                        }

                        // Device Router builds RbHeader
                        DeviceRouter dr = null;
                        if (devRouting)
                        {
                            dr = new DeviceRouter(rbh, sqlConnString);
                            rbh = dr.GetDeviceRouting();
                            string new_header = JsonConvert.SerializeObject(rbh);
                            jo_message[RbFormatType.RbHeader] = JsonConvert.DeserializeObject<JObject>(new_header);
                        }
                        else
                        {
                            rbh.TargetDeviceId = rbh.SourceDeviceId;
                            rbh.TargetType = RbTargetType.Device;
                        }

                        // Application Routing
                        JArray ja_messages = null;
                        if (appRouting)
                        {
                            // Application Call Logic
                            JObject jo_temp = (JObject)jo_message[RbFormatType.RbBody];
                            string rbBodyString = JsonConvert.SerializeObject(jo_temp);
                            ja_messages = CallApps(rbh, rbBodyString, context.Lease.PartitionId);
                        }
                        else if (rbh.RoutingType != RbRoutingType.CONTROL)
                        {
                            ja_messages = new JArray();
                            ja_messages.Add(jo_message);
                        }

                        // RoutingType="CONTROL" and AppProcessingId="ReqAppInfo" 
                        if (rbh.RoutingType == RbRoutingType.CONTROL && rbh.AppProcessingId == RbControlType.ReqAppInfo)
                        {
                            ja_messages = ProcessControlMessage(rbh);
                        }

                        // Send C2D Message
                        C2dMessageSender c2dsender = null;
                        if (rbh.RoutingType == RbRoutingType.CALL
                            || rbh.RoutingType == RbRoutingType.D2D
                            || rbh.RoutingType == RbRoutingType.CONTROL)
                        {
                            c2dsender = new C2dMessageSender(ja_messages, rbIotHubConnString, sqlConnString);
                            c2dsender.SendToDevice();
                        }

                        // C2D Message Logging to Event Hub
                        if (rbC2dLogEnabled)
                        {
                            //RbEventMessageLog rbEventMessageLog = new RbEventMessageLog();
                            //rbEventMessageLog.MessageType = RbMessageLogType.C2dType;
                            //rbEventMessageLog.SendUtcDateTime = DateTime.UtcNow;
                            //rbEventMessageLog.Messages = ja_messages;
                            //string str_messages = JsonConvert.SerializeObject(rbEventMessageLog);
                            RbEventHubs rbEventHubs = new RbEventHubs(rbC2dLogEventHubConnString, rbC2dLogEventHubName);
                            foreach (JObject jo in ja_messages)
                            {
                                string str_message = JsonConvert.SerializeObject(jo);
                                rbEventHubs.SendMessage(str_message, iothub_deviceId);
                            }
                        }

                    }
                    //catch (SqlException sqlex)
                    //{
                    //    sqlex_on = true;
                    //}
                    catch (Exception ex)
                    {
                        RbTraceLog.WriteError("E001", ex.ToString(), jo_message);
                        if (ex.GetType() == typeof(SqlException))  // "is" matches extended type as well
                            sqlex_on = true;
                    }
                }

                if (sqlex_on)
                {
                    sqlex_on = false;
                }
                else
                {
                    // go forward read pointer if not sqlexception
                    await context.CheckpointAsync();
                }
            }
        }

        async Task IEventProcessor.CloseAsync(PartitionContext context, CloseReason reason)
        {
            string pid = Thread.CurrentThread.ManagedThreadId.ToString();
            string msg = string.Format("RoboticsEventProcessor Shuting Down.  Host:{0}, Partition:{1}, Reason:{2}.",
                         Environment.MachineName, context.Lease.PartitionId, reason.ToString());
            Trace.TraceInformation(msg);

            if (reason == CloseReason.Shutdown)
            {
                //await context.CheckpointAsync();  //Shoud not checkpoint here
            }
        }

        JArray ProcessControlMessage(RbHeader rbh)
        {
            
            JArray ja_messages = new JArray();
            try
            {
                RbAppMasterCache rbappmc = GetAppMasterInfo(rbh);
                RbMessage message = new RbMessage();
                message.RbHeader = rbh;
                message.RbBody = JsonConvert.DeserializeObject<JObject>(rbappmc.AppInfoDevice);
                string json_message = JsonConvert.SerializeObject(message);
                JObject jo = (JObject)JsonConvert.DeserializeObject(json_message);
                ja_messages.Add(jo);
            }
            catch(Exception ex)
            {
                RbTraceLog.WriteError("E004", ex.ToString());
                ae = new ApplicationException("Error ** <<CONTROL Message Processing>> Exception occured during JSON processing on RBFX.AppMaster[AppInfoDevice]");
                throw ae;
            }
            return ja_messages;
        }

        JArray CallApps(RbHeader rbh, string rbBodyString, string partitionId)
        {
            // Get App Master Info
            RbAppMasterCache rbappmc = GetAppMasterInfo(rbh);

            // Get App Routing Info
            RbAppRouterCache rbapprc = GetAppRoutingInfo(rbh);

            JArrayString ja_messagesString = null;
            JArray ja_messages = null;
            string dllFilePath = string.Empty;

            IAppRouterDll routedAppDll = null;
            Assembly assembly = null;

            // Load DLL from BLOB
            string baseDirectory = string.Empty;
            string privateDllDirectory = string.Empty;
            string cachedFileName = string.Empty;
            string cachedFileNameWithoutExt = string.Empty;

            if (rbapprc.DevMode == "True")
            {
                string devdir = rbapprc.DevLocalDir;
                int pos = devdir.Length - 1;
                if (devdir.Substring(pos, 1) == @"\")
                {
                    dllFilePath = rbapprc.DevLocalDir + rbapprc.FileName;
                }
                else
                {
                    dllFilePath = rbapprc.DevLocalDir + @"\" + rbapprc.FileName;
                }

                baseDirectory = Path.GetDirectoryName(dllFilePath);
                privateDllDirectory = baseDirectory;
                cachedFileName = Path.GetFileName(dllFilePath);
                cachedFileNameWithoutExt = Path.GetFileNameWithoutExtension(dllFilePath);
            }
            else
            {
                CachedDllFileInfo cachedDllFileInfo = CopyBlobToLocalDir(rbappmc, rbapprc, partitionId);
                baseDirectory = cachedDllFileInfo.BaseDirectory;
                privateDllDirectory = cachedDllFileInfo.PrivateDllDirectory;
                cachedFileName = Path.GetFileName(cachedDllFileInfo.PrivateDllFilePath);
                cachedFileNameWithoutExt = Path.GetFileNameWithoutExtension(cachedDllFileInfo.PrivateDllFilePath);
            }

            ////Static load without AppDomain
            //assembly = System.Reflection.Assembly.LoadFrom(dllFilePath);
            //routedAppDll = assembly.CreateInstance(rbapprc.ClassName) as IAppRouterDll;

            //Dynamic load using AppDomain
            try
            {
                string appDomainName = appDomanNameBase + partitionId; 
                AppDomain appDomain = null;
                if (appDomainList.ContainsKey(partitionId))
                {
                    appDomain = appDomainList[partitionId];
                }

                if (appDomain == null)
                {
                    appDomain = CreateAppDomain(appDomainName, baseDirectory, privateDllDirectory);
                    lock (thisLock2)
                    {
                        appDomainList[partitionId] = appDomain;
                    }
                }
                routedAppDll = appDomain.CreateInstanceAndUnwrap(cachedFileNameWithoutExt, rbapprc.ClassName) as IAppRouterDll;
            }
            catch(Exception ex)
            {
                RbTraceLog.WriteError("E003", ex.ToString());
                ae = new ApplicationException("Error ** Exception occured during creating AppDomain & Instance(App DLL)");
                throw ae;
            }

            // ProcessMessage
            try
            {
                rbh.ProcessingStack = rbapprc.FileName;
                ja_messagesString = routedAppDll.ProcessMessage(rbappmc, rbapprc, rbh, rbBodyString);
                ja_messages = ja_messagesString.ConvertToJArray();
            }
            catch(Exception ex)
            {
                RbTraceLog.WriteError("E002", ex.ToString());
                ae = new ApplicationException("Error ** Exception occured in routed App DLL");
                throw ae;
            }

            return ja_messages;
        }

        RbAppMasterCache GetAppMasterInfo(RbHeader rbh)
        {
            AppMaster am = null;
            bool am_action = true;
            RbAppMasterCache rbappmc = null;
            if (rbAppMasterCacheDic.ContainsKey(rbh.AppId))
            {
                rbappmc = (RbAppMasterCache)rbAppMasterCacheDic[rbh.AppId];
                if (rbappmc.CacheExpiredDatetime >= DateTime.Now)
                    am_action = false;
            }
            if (am_action)
            {
                am = new AppMaster(rbh.AppId, rbEncPassPhrase, rbSqlConnectionString, rbCacheExpiredTimeSec);
                rbappmc = am.GetAppMaster();
                if (rbappmc != null)
                {
                    lock (thisLock)
                    {
                        rbAppMasterCacheDic[rbh.AppId] = rbappmc;
                    }
                }
                else
                {
                    ae = new ApplicationException("Error ** GetAppMaster() returns Null Object");
                    throw ae;
                }
            }

            return rbappmc;
        }

        RbAppRouterCache GetAppRoutingInfo(RbHeader rbh)
        {
            AppRouter ar = null;
            bool ar_action = true;
            string cachekey = rbh.AppId + "_" + rbh.AppProcessingId;
            RbAppRouterCache rbapprc = null;
            if (rbAppRouterCacheDic.ContainsKey(cachekey))
            {
                rbapprc = (RbAppRouterCache)rbAppRouterCacheDic[cachekey];
                if (rbapprc.CacheExpiredDatetime >= DateTime.Now)
                    ar_action = false;
            }
            if (ar_action)
            {
                ar = new AppRouter(rbh.AppId, rbh.AppProcessingId, rbSqlConnectionString, rbCacheExpiredTimeSec);
                rbapprc = ar.GetAppRouting();
                if (rbapprc != null)
                {
                    lock (thisLock)
                    {
                        rbAppRouterCacheDic[cachekey] = rbapprc;
                    }
                }
                else
                {
                    ae = new ApplicationException("Error ** GetAppRouting() returns Null Object");
                    throw ae;
                }
            }

            return rbapprc;
        }

        CachedDllFileInfo CopyBlobToLocalDir(RbAppMasterCache rbappmc, RbAppRouterCache rbapprc, string partitionId)
        {
            //string curdir = Environment.CurrentDirectory;
            CachedDllFileInfo cachedDllFileInfo = new CachedDllFileInfo();
            string curdir = AppDomain.CurrentDomain.BaseDirectory;
            cachedDllFileInfo.BaseDirectory = curdir;
            cachedDllFileInfo.PrivateDllDirectory = Path.Combine(curdir, "P" + partitionId);

            string blobTargetFilePath = string.Empty;
            RbAppDllCacheInfo rbAppDllInfo = null;
            RbAppDllCacheInfo rbAppDllInfo_partition = null;
            bool loadAction = true;
            bool blobCopyAction = true;
            string partitionedFileNameKey = "P" + partitionId + "_" + rbapprc.FileName;

            // Check original DLL info
            if (rbAppDllCacheInfoDic.ContainsKey(rbapprc.FileName))
            {
                // Original DLL
                rbAppDllInfo = (RbAppDllCacheInfo)rbAppDllCacheInfoDic[rbapprc.FileName];
                blobTargetFilePath = Path.Combine(rbAppDllInfo.CacheDir, rbAppDllInfo.CachedFileName);

                // Use cached original DLL if Registered_Datetime not changed.
                if (rbAppDllInfo.AppId == rbapprc.AppId
                    && rbAppDllInfo.AppProcessingId == rbapprc.AppProcessingId
                    && rbAppDllInfo.Registered_DateTime == rbapprc.Registered_DateTime)
                {
                    blobCopyAction = false;
                }
            }

            // Check partitioned DLL info
            if (rbAppDllCacheInfoDic.ContainsKey(partitionedFileNameKey))
            {
                // DLL copied into each partition directory
                rbAppDllInfo_partition = (RbAppDllCacheInfo)rbAppDllCacheInfoDic[partitionedFileNameKey];
                cachedDllFileInfo.PrivateDllFilePath = Path.Combine(rbAppDllInfo_partition.CacheDir, rbAppDllInfo_partition.CachedFileName);

                // Use cached DLL copied into each partition directory if Registered_Datetime not changed.
                if (rbAppDllInfo_partition.AppId == rbapprc.AppId
                    && rbAppDllInfo_partition.AppProcessingId == rbapprc.AppProcessingId
                    && rbAppDllInfo_partition.Registered_DateTime == rbapprc.Registered_DateTime)
                {
                    loadAction = false;
                }
            }

            if (loadAction)
            {
                if (blobTargetFilePath != string.Empty)
                {
                    AppDomain appDomain = null; 
                    if (appDomainList.ContainsKey(partitionId))
                    {
                        appDomain = appDomainList[partitionId];
                        AppDomain.Unload(appDomain);
                        lock (thisLock2)
                        {
                            appDomainList[partitionId] = null;
                        }
                    }

                    if (blobCopyAction)
                    {
                        // Move current DLL to archive directory
                        if (File.Exists(blobTargetFilePath))
                        {
                            string archivedDirectory = Path.Combine(curdir, archivedDirectoryName);
                            string archivedDllFilePath = archivedDirectory + @"\" + rbapprc.FileName
                                                       + ".bk" + DateTime.Now.ToString("yyyyMMddHHmmss");
                            if (!Directory.Exists(archivedDirectory))
                            {
                                Directory.CreateDirectory(archivedDirectory);
                            }
                            File.Move(blobTargetFilePath, archivedDllFilePath);
                        }
                    }
                }

                if (blobCopyAction)
                {
                    // Download DLL from BLOB
                    RbAzureStorage rbAzureStorage = new RbAzureStorage(rbappmc.StorageAccount, rbappmc.StorageKey);
                    rbAppDllInfo = new RbAppDllCacheInfo();
                    rbAppDllInfo.FileName = rbapprc.FileName;
                    rbAppDllInfo.CacheDir = Path.Combine(curdir, "cache");
                    if (!Directory.Exists(rbAppDllInfo.CacheDir))
                        Directory.CreateDirectory(rbAppDllInfo.CacheDir);
                    rbAppDllInfo.AppId = rbapprc.AppId;
                    rbAppDllInfo.AppProcessingId = rbapprc.AppProcessingId;
                    rbAppDllInfo.Registered_DateTime = rbapprc.Registered_DateTime;
                    //rbAppDllInfo.GenerateCachedFileName();
                    rbAppDllInfo.CachedFileName = rbAppDllInfo.FileName;
                    blobTargetFilePath = Path.Combine(rbAppDllInfo.CacheDir, rbAppDllInfo.CachedFileName);

                    using (var fileStream = File.OpenWrite(blobTargetFilePath))
                    {
                        rbAzureStorage.BlockBlobDownload(fileStream, rbapprc.BlobContainer, rbapprc.FileName);
                    }

                    // Update cache info if DLL download from BLOB is successful.
                    lock (thisLock2)
                    {
                        rbAppDllCacheInfoDic[rbapprc.FileName] = rbAppDllInfo;
                    }

                    // Logging
                    if (rbTraceLevel == RbTraceType.Detail)
                    {
                        RbTraceLog.WriteLog(string.Format("App DLL is copied from BLOB strage.  Dir:{0}, FileName:{1}",
                                     curdir, rbAppDllInfo.CachedFileName));
                    }
                }

                // Copy original DLL into partition directory
                rbAppDllInfo_partition = new RbAppDllCacheInfo();
                rbAppDllInfo_partition.FileName = rbapprc.FileName;
                rbAppDllInfo_partition.CacheDir = cachedDllFileInfo.PrivateDllDirectory;
                rbAppDllInfo_partition.AppId = rbapprc.AppId;
                rbAppDllInfo_partition.AppProcessingId = rbapprc.AppProcessingId;
                rbAppDllInfo_partition.Registered_DateTime = rbapprc.Registered_DateTime;
                rbAppDllInfo_partition.CachedFileName = rbAppDllInfo_partition.FileName;

                string sourceFilePath = Path.Combine(rbAppDllInfo.CacheDir, rbAppDllInfo.CachedFileName);
                string targetFilePath = Path.Combine(rbAppDllInfo_partition.CacheDir, rbAppDllInfo_partition.CachedFileName);
                cachedDllFileInfo.PrivateDllFilePath = targetFilePath;
                if (!Directory.Exists(rbAppDllInfo_partition.CacheDir))
                    Directory.CreateDirectory(rbAppDllInfo_partition.CacheDir);
                File.Copy(sourceFilePath, targetFilePath, true);

                // Update cache info if DLL copied successfully.
                lock (thisLock2)
                {
                    rbAppDllCacheInfoDic[partitionedFileNameKey] = rbAppDllInfo_partition;
                }

                // Logging
                if (rbTraceLevel == RbTraceType.Detail)
                {
                    RbTraceLog.WriteLog(string.Format("Original App DLL is copied into partition directory.  Dir:{0}, FileName:{1}, PartitionId:{2}",
                                 curdir, rbAppDllInfo.CachedFileName, partitionId));
                }
            }

            return cachedDllFileInfo;
        }

        private class CachedDllFileInfo
        {
            public string BaseDirectory { set; get; }
            public string PrivateDllDirectory { set; get; }
            public string PrivateDllFilePath { set; get; }
        }

        AppDomain CreateAppDomain(string appName, string baseDirectory, string privateDllDirectory)
        {
            AppDomainSetup setup = new AppDomainSetup();
            setup.ApplicationName = appName;
            setup.ApplicationBase = baseDirectory;       //AppDomain.CurrentDomain.BaseDirectory
            setup.PrivateBinPath = privateDllDirectory;
            setup.CachePath = Path.Combine(privateDllDirectory, "cache" + Path.DirectorySeparatorChar);
            setup.ShadowCopyFiles = "true";
            setup.ShadowCopyDirectories = privateDllDirectory;

            AppDomain appDomain = AppDomain.CreateDomain(appName, null, setup);

            return appDomain;
        }

    }
}
