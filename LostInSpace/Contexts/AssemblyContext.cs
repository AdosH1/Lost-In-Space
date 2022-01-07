using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xamarin.Forms;
using System.Linq;

namespace Prominence.Contexts
{
    public static class AssemblyContext
    {
        public static System.Reflection.Assembly Assembly { get; set; }
        public static System.Resources.ResourceManager ResourceManager { get; set; }

        public static void Initialise(string projectName)
        {
            Assembly = System.Reflection.Assembly.GetExecutingAssembly();
            //var test = AppDomain.CurrentDomain.GetAssemblies();
            Assembly = AppDomain.CurrentDomain.GetAssemblies().Where(x => x.FullName.Contains(projectName)).First();
            string resourceName = Assembly.GetName().Name + ".Properties.Resources";
            //string resourceName = Assembly.GetName().Name + ".Properties";
            
            ResourceManager = new System.Resources.ResourceManager(resourceName, Assembly);
        }

        public static ImageSource GetImageByName(string imageName)
        {
            var obj = ResourceManager.GetObject(imageName);
            if (obj != null)
            {
                var bytes = (byte[])obj;
                var im = ImageSource.FromStream(() => new MemoryStream(bytes));
                return im;
            }
            return null;
        }

        public static System.IO.Stream GetStreamByName(string fileName)
        {
            var obj = ResourceManager.GetObject(fileName);
            if (obj != null)
            {
                var bytes = (byte[])obj;
                MemoryStream stream = new MemoryStream(bytes);
                return stream;
            }
            return null;
        }

    }
}
