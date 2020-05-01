#define TEST_AS_CONSOLE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
//using System.Threading.Tasks;

namespace RFIDTag
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
        {
            ServiceBase[] ServicesToRun;
            RFIDService.RFIDService service = new RFIDService.RFIDService();
            
            //string resource = "RFIDTag.BlowFishClassLibrary.dll";
            //EmbeddedAssembly.Load(resource, "BlowFishClassLibrary.dll");
            //AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(CurrentDomain_AssemblyResolve);
            
            if (Environment.UserInteractive)
            {
                ServicesToRun = new ServiceBase[]
                {
                    new RFIDService.RFIDService()
                };
                service.RunAsConsole(args);
            }
            else
            {
                ServicesToRun = new ServiceBase[]
                {
                    new RFIDService.RFIDService()
                    //service 
                };
                ServiceBase.Run(ServicesToRun);
            }
        }
        static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            return EmbeddedAssembly.Get(args.Name);
        }
    }
}
