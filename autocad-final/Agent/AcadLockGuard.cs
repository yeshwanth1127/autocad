using System;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using autocad_final.AreaWorkflow;
using autocad_final.Geometry;

namespace autocad_final.Agent
{
    /// <summary>
    /// Centralized document lock and transaction guard for agent write operations.
    /// </summary>
    public static class AcadLockGuard
    {
        /// <summary>
        /// Marshals <paramref name="func"/> onto the Win32 message-pump thread (T001) via
        /// <paramref name="invokeTarget"/>.Invoke and returns a Task that completes with the result.
        /// Must be awaited from a background thread. Uses Control.Invoke (not
        /// ExecuteInApplicationContext) so AutoCAD's Idle/reactor system stays on T001.
        /// </summary>
        public static Task<T> RunOnAcadThread<T>(System.Windows.Forms.Control invokeTarget, Func<T> func)
        {
            if (invokeTarget == null) throw new ArgumentNullException(nameof(invokeTarget));
            var tcs = new TaskCompletionSource<T>();

            invokeTarget.Invoke(new Action(() =>
            {
                try   { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));

            return tcs.Task;
        }

        public static T RunWithLock<T>(Document doc, Func<Transaction, T> work)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (work == null) throw new ArgumentNullException(nameof(work));

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                try
                {
                    EnsurePreflightEntities(tr, doc.Database);
                    var result = work(tr);
                    tr.Commit();
                    return result;
                }
                catch
                {
                    tr.Abort();
                    throw;
                }
            }
        }

        public static Task<T> RunWithLockAsync<T>(Document doc, Func<Transaction, T> work)
        {
            // Current plugin execution model is synchronous; this keeps async call sites simple.
            return Task.FromResult(RunWithLock(doc, work));
        }

        private static void EnsurePreflightEntities(Transaction tr, Database db)
        {
            SprinklerXData.EnsureRegApp(tr, db);
            SprinklerLayers.EnsureAll(tr, db);
        }
    }
}
