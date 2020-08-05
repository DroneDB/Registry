using System;
using System.Collections.Generic;
using System.Text;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Model;

namespace Registry.Adapters.DroneDB
{
    public class Ddb : IDdb
    {

        // TODO: This class should connect to ddb sqlite database and extract info
        public Ddb(string dbPath)
        {

        }

        public DdbObject GetObjectInfo(int id)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<DdbObject> Search(string path)
        {
            throw new NotImplementedException();
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // stream.Dispose();
                }

                _disposed = true;
            }
        }

        ~Ddb()
        {
            Dispose(false);
        }

        #endregion
    }
}
