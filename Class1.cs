using Atlassian.Jira;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JiraMigration
{
    public class Program
    {   
        // TODO: Provide these const string VstsUrl = "https://{AzureDevOps Organization}.visualstudio.com";         const string VstsPAT = "{AzureDevOps Personal Access Token}";         const string VstsProject = "{AzureDevOps Project Name}";

        const string JiraUserID = "{Jira local username}"; 
        const string JiraPassword = "{Jira local password}"; 
        const string JiraUrl = "{Jira instance url}"; 
        const string JiraProject = "{Jira Project abbreviation}";         
        
        // END TODO

        // These are to provide the ability to resume a migration if an error occurs.         //         static string MigratedPath = Path.Combine(Environment.CurrentDirectory, "..", "..", "migrated.json");         static Dictionary Migrated = File.Exists(MigratedPath) ? JsonConvert.DeserializeObject>(File.ReadAllText(MigratedPath)) : new Dictionary();

        public static void Main(string[] args) => Execute().GetAwaiter().GetResult(); 
        public static async Task Execute()
        {
            var vstsConnection = new VssConnection(new Uri(VstsUrl), new VssBasicCredential(string.Empty, VstsPAT)); 
            
            var witClient = vstsConnection.GetClient();

            var jiraConn = Jira.CreateRestClient(JiraUrl, JiraUserID, JiraPassword);

            var issues = jiraConn.Issues.Queryable.Where(p => p.Project == JiraProject).Take(Int32.MaxValue).ToList();

            // By default this will root the migrated items at the root of Vsts project             
            // Uncomment ths line and provide an epic id if you want everything to be             
            // a child of Vsts epic                          
            //AddMigrated(JiraProject, {VstsEpic Id});
            //foreach (var feature in issues.Where(p => p.Type.Name == "Epic"))
            //await CreateFeature(witClient, feature);
            //foreach (var bug in issues.Where(p => p.Type.Name == "Bug"))
            //await CreateBug(witClient, bug, JiraProject);
            //foreach (var backlogItem in issues.Where(p => p.Type.Name == "Story"))
            //await CreateBacklogItem(witClient, backlogItem, JiraProject);
            //foreach (var task in issues.Where(p => p.Type.Name == "Task" || p.Type.Name == "Sub-task"))                 await CreateTask(witClient, task, JiraProject);        }

            static Task CreateFeature(WorkItemTrackingHttpClient client, Issue jira) => CreateWorkItem(client, "Feature", jira, jira.Project, jira.CustomFields["Epic Name"].Values[0], jira.Description ?? jira.Summary, ResolveFeatureState(jira.Status)); 
            static Task CreateBug(WorkItemTrackingHttpClient client, Issue jira, string defaultParentKey) => CreateWorkItem(client, "Bug", jira, jira.CustomFields["Epic Link"]?.Values[0] ?? defaultParentKey, jira.Summary, jira.Description, ResolveBacklogItemState(jira.Status)); 
            static Task CreateBacklogItem(WorkItemTrackingHttpClient client, Issue jira, string defaultParentKey) => CreateWorkItem(client, "Product Backlog Item", jira, jira.CustomFields["Epic Link"]?.Values[0] ?? defaultParentKey, jira.Summary, jira.Description, ResolveBacklogItemState(jira.Status), new JsonPatchOperation { Path = "/fields/Microsoft.VSTS.Scheduling.Effort", Value = jira.CustomFields["Story Points"]?.Values[0] }); 
            static Task CreateTask(WorkItemTrackingHttpClient client, Issue jira, string defaultParentKey) => CreateWorkItem(client, "Task", jira, jira.ParentIssueKey ?? defaultParentKey, jira.Summary, jira.Description, ResolveTaskState(jira.Status)); 
            static async Task CreateWorkItem(WorkItemTrackingHttpClient client, string type, Issue jira, string parentKey, string title, string description, string state, params JsonPatchOperation[] fields)
            {             // Short-circuit if we've already projcessed this item.             //             if (Migrated.ContainsKey(jira.Key.Value)) return;

                var vsts = new JsonPatchDocument { new JsonPatchOperation { Path = "/fields/System.State", Value = state }, 
                    new JsonPatchOperation { Path = "/fields/System.CreatedBy", Value = ResolveUser(jira.Reporter) }, 
                    new JsonPatchOperation { Path = "/fields/System.CreatedDate", Value = jira.Created.Value.ToUniversalTime() }, 
                    new JsonPatchOperation { Path = "/fields/System.ChangedBy", Value = ResolveUser(jira.Reporter) }, 
                    new JsonPatchOperation { Path = "/fields/System.ChangedDate", Value = jira.Created.Value.ToUniversalTime() }, 
                    new JsonPatchOperation { Path = "/fields/System.Title", Value = title }, 
                    new JsonPatchOperation { Path = "/fields/System.Description", Value = description }, 
                    new JsonPatchOperation { Path = "/fields/Microsoft.VSTS.Common.Priority", Value = ResolvePriority(jira.Priority) } }; 
                if (parentKey != null) vsts.Add(new JsonPatchOperation { Path = "/relations/-", Value = new WorkItemRelation { Rel = "System.LinkTypes.Hierarchy-Reverse", Url = $"https://ciappdev.visualstudio.com/_apis/wit/workItems/{Migrated[parentKey]}" } }); 
                if (jira.Assignee != null) vsts.Add(new JsonPatchOperation { Path = "/fields/System.AssignedTo", Value = ResolveUser(jira.Assignee) }); 
                if (jira.Labels.Any()) vsts.Add(new JsonPatchOperation { Path = "/fields/System.Tags", Value = jira.Labels.Aggregate("", (l, r) => $"{l}; {r}").Trim(';', ' ') }); 
                foreach (var attachment in await jira.GetAttachmentsAsync()) { var bytes = await attachment.DownloadDataAsync(); 
                    using (var stream = new MemoryStream(bytes)) 
                    { 
                        var uploaded = await client.CreateAttachmentAsync(stream, VstsProject, fileName: attachment.FileName); 
                        vsts.Add(new JsonPatchOperation { Path = "/relations/-", Value = new WorkItemRelation { Rel = "AttachedFile", Url = uploaded.Url } }); 
                    } 
                }

                var all = vsts.Concat(fields).Where(p => p.Value != null).ToList(); vsts = new JsonPatchDocument(); vsts.AddRange(all); var workItem = await client.CreateWorkItemAsync(vsts, VstsProject, type, bypassRules: true); AddMigrated(jira.Key.Value, workItem.Id.Value);

                await CreateComments(client, workItem.Id.Value, jira);

                Console.WriteLine($"Added {type}: {jira.Key} {title}");
            }
            static async Task CreateComments(WorkItemTrackingHttpClient client, int id, Issue jira) 
            { 
                var comments = (await jira.GetCommentsAsync()).Select(p => CreateComment(p.Body, p.Author, p.CreatedDate?.ToUniversalTime())).Concat(new[] { CreateComment($"Migrated from {jira.Key}") }).ToList(); 
                foreach (var comment in comments) await client.UpdateWorkItemAsync(comment, id, bypassRules: true); 
            }

            static JsonPatchDocument CreateComment(string comment, string username = null, DateTime? date = null)
            {
                var patch = new JsonPatchDocument { new JsonPatchOperation { Path = "/fields/System.History", Value = comment } }; 
                if (username != null) 
                    patch.Add(new JsonPatchOperation { Path = "/fields/System.ChangedBy", Value = ResolveUser(username) }); 
                if (date != null) 
                    patch.Add(new JsonPatchOperation { Path = "/fields/System.ChangedDate", Value = date?.ToUniversalTime() });

                return patch;
            }

            static void AddMigrated(string jira, int vsts)
            {
                if (Migrated.ContainsKey(jira)) return;

                Migrated.Add(jira, vsts); 

                File.WriteAllText(MigratedPath, JsonConvert.SerializeObject(Migrated));
            }
            static string ResolveUser(string user) { 
                // Provide your own user mapping             //
                // switch (user)
                // {                 case "anna.banana": return "anna.banana@contoso.com";
                // default: throw new ArgumentException("Could not find user", nameof(user));
                // }         }         static string ResolveFeatureState(IssueStatus state)
                // {             // Customize if your Vsts project uses custom task states.             //
                // switch (state.Name)
                // {                 case "Needs Approval": return "New";
                // case "Ready for Review": return "In Progress";
                // case "Closed": return "Done";
                // case "Resolved": return "Done";
                // case "Reopened": return "New";
                // case "In Progress": return "In Progress";
                // case "Backlog": return "New";
                // case "Selected for Development": return "New";
                // case "Open": return "New";
                // case "To Do": return "New";
                // case "DONE": return "Done";
                // default: throw new ArgumentException("Could not find state", nameof(state));
                // }         }         static string ResolveBacklogItemState(IssueStatus state)
                // {             // Customize if your Vsts project uses custom task states.             //
                // switch (state.Name)             {                 case "Needs Approval": return "New";
                // case "Ready for Review": return "Committed";
                // case "Closed": return "Done";
                // case "Resolved": return "Done";
                // case "Reopened": return "New";
                // case "In Progress": return "Committed";
                // case "Backlog": return "New";
                // case "Selected for Development": return "Approved";
                // case "Open": return "Approved";
                // case "To Do": return "New";
                // case "DONE": return "Done";
                // default: throw new ArgumentException("Could not find state", nameof(state));
                // }         }         static string ResolveTaskState(IssueStatus state)
                // {             // Customize if your Vsts project uses custom task states.             //
                // switch (state.Name)             {
                // case "Needs Approval": return "To Do";
                // case "Ready for Review": return "In Progress";
                // case "Closed": return "Done";
                // case "Resolved": return "Done";
                // case "Reopened": return "To Do";
                // case "In Progress": return "In Progress";
                // case "Backlog": return "To Do";
                // case "Selected for Development": return "To Do";
                // case "Open": return "To Do";
                // case "To Do": return "To Do";
                // case "DONE": return "Done";
                // default: throw new ArgumentException("Could not find state", nameof(state));             }
                // }         static int ResolvePriority(IssuePriority priority)
                // {             switch (priority.Name)
                // {                 case "Low-Minimal business impact": return 4;
                // case "Medium-Limited business impact": return 3;
                // case "High-Significant business impact": return 2;
                // case "Urgent- Critical business impact": return 1;
                // default: throw new ArgumentException("Could not find priority", nameof(priority));
                // }         }     } }