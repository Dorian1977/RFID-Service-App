﻿using RFIDTag;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
//using System.Threading.Tasks;

namespace RFIDService
{
    public partial class RFIDService : ServiceBase
    {
        RFIDMain RFIDmain = new RFIDMain();
        public RFIDService()
        {
            InitializeComponent();
        }

        public void RunAsConsole(string[] args)
        {
            OnStart(args);
        }

        public static void StartService(string serviceName, int timeoutMilliseconds)
        {
            ServiceController service = new ServiceController(serviceName);
            try
            {
                TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);

                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, timeout);
            }
            catch
            {
                // ...
            }
        }
        
        public void StopService(string serviceName, int timeoutMilliseconds)
        {
            ServiceController service = new ServiceController(serviceName);
            try
            {
                if(service.Status == ServiceControllerStatus.Running)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped);
                }
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine("Could not stop the {0} service.", serviceName);
                Console.WriteLine(e.Message);
            }
        }

        public bool serviceExists(string ServiceName)
        {
            return ServiceController.GetServices().Any(serviceController => serviceController.ServiceName.Equals(ServiceName));
        }
        /*
        public void RestartService(string serviceName, int timeoutMilliseconds)
        {
            ServiceController service = new ServiceController(serviceName);
            try
            {
                if(serviceExists(serviceName))
                {
                    if (service.Status == ServiceControllerStatus.Running)
                    {
                        int millisec1 = Environment.TickCount;
                        TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);

                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);

                        // count the rest of the timeout
                        int millisec2 = Environment.TickCount;
                        timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds - (millisec2 - millisec1));

                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    }
                    else
                    {
                        int millisec1 = Environment.TickCount;
                        TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);

                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    }
                }               
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine("Could not restart the {0} service.", serviceName);
                Console.WriteLine(e.Message);
            }
        }
        */
        protected override void OnStart(string[] args)
        { 
            while(!RFIDmain.run())
            {
                Thread.Sleep(500);
            }
            StartService(this.ServiceName, 3000);            
        }

        protected override void OnStop()
        {
            RFIDmain.RFIDStop();
            StopService(this.ServiceName, 3000);            
        }

        protected override void OnShutdown()
        {
            RFIDmain.RFIDStop();
            base.OnShutdown();
        }
    }
}