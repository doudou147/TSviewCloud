﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TSviewCloudConfig;

namespace TSviewCloudPlugin
{
    public class ItemControl
    {
        internal static SynchronizationContext synchronizationContext;

        static ConcurrentDictionary<string, int> _ReloadRequest = new ConcurrentDictionary<string, int>();
        public static ConcurrentDictionary<string, int> ReloadRequest { get => _ReloadRequest; set => _ReloadRequest = value; }


        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        static public string GetLongFilename(string filename)
        {
            if (filename.StartsWith(@"\\")) return filename;
            return @"\\?\" + filename;
        } 
            

        static public string GetOrgFilename(string Longfilename)
        {
            if (Longfilename.StartsWith(@"\\?\")) return Longfilename.Substring(4);
            return Longfilename;
        }

        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        static private void MakeSureItem(IRemoteItem item)
        {
            item = RemoteServerFactory.PathToItem(item.FullPath);
            if (item.Children?.Count() != 0)
            {
                foreach (var c in item.Children)
                {
                    MakeSureItem(c);
                }
            }
        }

        static private Job[] __DoDownloadFolder(string localfoldername, IEnumerable<IRemoteItem> remoteItems, Job prevJob = null)
        {
            var ret = new List<Job>();
            foreach (var item in remoteItems)
            {
                if (item.ItemType == RemoteItemType.File)
                {
                    prevJob = DownloadFile(Path.Combine(localfoldername, item.Name), item, prevJob: prevJob, weekdepend: true);
                    ret.Add(prevJob);
                }
                else
                {
                    var dname = Path.Combine(localfoldername, item.Name);
                    Directory.CreateDirectory(GetLongFilename(dname));
                    ret.AddRange(__DoDownloadFolder(dname, item.Children, prevJob));
                }
            }
            return ret.ToArray();
        }

        static private Job DoDownloadFolder(string localfoldername, IEnumerable<IRemoteItem> remoteItems, Job prevJob = null)
        {
            var job = JobControler.CreateNewJob(JobClass.ControlMaster, depends: prevJob);
            job.DisplayName = "Download items";
            JobControler.Run(job, (j) =>
            {
                Task.WaitAll(__DoDownloadFolder(localfoldername, remoteItems, j).Select(x => x.WaitTask(ct: j.Ct)).ToArray(), j.Ct);
            });
            return job;
        }

        static public Job DownloadFile(string localfilename,  IRemoteItem remoteItem, Job prevJob = null, bool weekdepend = false)
        {
            Config.Log.LogOut("Download : " + remoteItem.Name);

            var download = remoteItem.DownloadItemRawJob(prevJob: prevJob);
            download.WeekDepend = weekdepend;

            var job = JobControler.CreateNewJob<Stream>(
                 type: JobClass.Download,
                 info: new JobControler.SubInfo
                 {
                     type = JobControler.SubInfo.SubType.DownloadFile,
                     size = remoteItem?.Size ?? 0,
                 },
                 depends: download);
            job.DisplayName = remoteItem.Name;
            job.ProgressStr = "Wait for download";

            JobControler.Run<Stream>(job, (j) =>
            {
                var result = j.ResultOfDepend[0];
                if(result.TryGetTarget(out var remotestream))
                {
                    using (remotestream)
                    {
                        FileStream outfile;
                        try
                        {
                            outfile = new FileStream(GetLongFilename(localfilename), FileMode.CreateNew);
                        }
                        catch (IOException)
                        {
                            synchronizationContext.Send((o) =>
                            {
                                var ans = MessageBox.Show("Override file? " + localfilename, "File already exists", MessageBoxButtons.YesNoCancel);
                                if (ans == DialogResult.Cancel)
                                    throw new OperationCanceledException("User cancel");
                                if (ans == DialogResult.No)
                                    j.Cancel();
                            }, null);

                            if (j.IsCanceled) return;
                            outfile = new FileStream(GetLongFilename(localfilename), FileMode.Create);
                        }
                        using (outfile)
                        {
                            try
                            {
                                using (var th = new ThrottleDownloadStream(remotestream, job.Ct))
                                using (var f = new PositionStream(th, remoteItem.Size ?? 0))
                                {
                                    f.PosChangeEvent += (src, evnt) =>
                                    {
                                        j.Progress = (double)evnt.Position / evnt.Length;
                                        j.ProgressStr = evnt.Log;
                                        j.JobInfo.pos = evnt.Position;
                                    };
                                    j.Ct.ThrowIfCancellationRequested();
                                    f.CopyToAsync(outfile, Config.DownloadBufferSize, j.Ct).Wait(j.Ct);
                                }
                                Config.Log.LogOut("Download : Done");
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                Config.Log.LogOut("Download : Error " + ex.ToString());
                                JobControler.ErrorOut("Download : Error {0}\n{1}", remoteItem.Name, ex.ToString());
                                j.ProgressStr = "Error detected.";
                                j.Progress = double.NaN;
                            }
                        }
                    }
                }

                job.ProgressStr = "done.";
                job.Progress = 1;
            });
            return job;
        }


        static public Job DownloadFolder(string localfoldername, IEnumerable<IRemoteItem> remoteItems, Job prevJob = null)
        {
            var items = remoteItems.ToArray();

            var job = JobControler.CreateNewJob(JobClass.LoadItem, depends: prevJob);
            job.DisplayName = "Search Items";
            JobControler.Run(job, (j) =>
            {
                foreach (var item in items)
                {
                    MakeSureItem(item);
                }
            });
            return DoDownloadFolder(localfoldername, items, job);
        }

        //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////


        static public IEnumerable<Job<IRemoteItem>> UploadFiles(IRemoteItem targetItem, IEnumerable<string> uploadFilenames, bool WeekDepend = false, params Job[] parentJob)
        {
            var joblist = new List<Job<IRemoteItem>>();
            if (uploadFilenames == null) return joblist;

            foreach(var upfile in uploadFilenames)
            {
                var job = targetItem.UploadFile(upfile, WeekDepend: WeekDepend, parentJob: parentJob);
                job.DisplayName = string.Format("Upload File {0} to {1}", upfile, targetItem.FullPath);
                joblist.Add(job);
            }
            return joblist;
        }

        static public Job<IRemoteItem> UploadFolder(IRemoteItem targetItem, string uploadFolderName, bool WeekDepend = false, Job prevJob = null)
        {
            var job = JobControler.CreateNewJob<IRemoteItem>(
                type: JobClass.Upload,
                info: new JobControler.SubInfo
                {
                    type = JobControler.SubInfo.SubType.UploadDirectory,
                },
                depends: targetItem.MakeFolder(Path.GetFileName(uploadFolderName), WeekDepend, prevJob));
            job.DisplayName = string.Format("Upload Folder {0} to {1}", uploadFolderName, targetItem.FullPath);
            JobControler.Run<IRemoteItem>(job, (j) =>
            {
                var result = j.ResultOfDepend[0];
                if (result.TryGetTarget(out var folder))
                {
                    j.Result = folder;

                    j.Progress = -1;
                    j.ProgressStr = "upload...";

                    var joblist = new List<Job<IRemoteItem>>();
                    joblist.AddRange(Directory.EnumerateDirectories(GetLongFilename(uploadFolderName)).Select(x => UploadFolder(folder, GetOrgFilename(x), true, j)));
                    joblist.AddRange(UploadFiles(folder, Directory.EnumerateFiles(GetLongFilename(uploadFolderName)), true, j));
                }
                //Parallel.ForEach(joblist, (x)=>x.Wait(ct: job.Ct));
                j.ProgressStr = "done";
                j.Progress = 1;
            });

            return job;
        }

    }
}