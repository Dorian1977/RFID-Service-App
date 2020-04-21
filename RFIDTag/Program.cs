#define TEST_AS_CONSOLE
using System;
using System.Collections.Generic;
using System.Linq;
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
    }
}
