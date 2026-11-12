using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskTimerWidget.Models;
using TaskTimerWidget.Services;
using TaskTimerWidget.Helpers;
using Serilog;

namespace TaskTimerWidget.ViewModels
{
    /// <summary>
    /// Main ViewModel managing application state and task operations.
    /// Handles timer coordination and task management.
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly ITaskService _taskService;
        private TaskViewModel? _activeTask;
        private bool _isLoading;
        private string? _errorMessage;
        private DispatcherTimer? _timerUpdate;
        private TaskViewModel? _selectedTask;

        private ICommand? _addTaskCommand;
        private ICommand? _selectTaskCommand;

        public ObservableCollection<TaskViewModel> Tasks { get; }

        public TaskViewModel? ActiveTask
        {
            get => _activeTask;
            private set => SetProperty(ref _activeTask, value);
        }

        public TaskViewModel? SelectedTask
        {
            get => _selectedTask;
            set => SetProperty(ref _selectedTask, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value);
        }

        public ICommand AddTaskCommand =>
            _addTaskCommand ??= new RelayCommand<string>(async (taskName) =>
            {
                if (!string.IsNullOrWhiteSpace(taskName))
                {
                    await AddTaskAsync(taskName);
                }
            });

        public ICommand SelectTaskCommand =>
            _selectTaskCommand ??= new RelayCommand<TaskViewModel>(async (taskVm) =>
            {
                if (taskVm != null)
                {
                    await SelectTaskAsync(taskVm);
                }
            });

        public MainViewModel(ITaskService taskService)
        {
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
            Tasks = new ObservableCollection<TaskViewModel>();

            // Initialize timer for updating UI
            InitializeTimer();
        }

        /// <summary>
        /// Initializes the ViewModel and loads existing tasks.
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                IsLoading = true;
                ErrorMessage = null;

                // Load tasks from service
                var tasks = await _taskService.GetAllTasksAsync();

                // Sort by Order property (ascending), then by CreatedAt for ties
                var sortedTasks = tasks.OrderBy(t => t.Order).ThenBy(t => t.CreatedAt);

                foreach (var task in sortedTasks)
                {
                    var viewModel = new TaskViewModel(task, _taskService);
                    viewModel.OnTaskDeleted += (sender, id) => OnTaskDeleted(id);
                    Tasks.Add(viewModel);
                }

                var running = Tasks.Where(t => t.IsRunning).OrderByDescending(t => t.GetModel().ModifiedAt).FirstOrDefault();
                foreach (var task in Tasks.Where(t => t != running && t.IsRunning))
                {
                    task.IsRunning = false;
                    task.GetModel().RunningSinceUtc = null;
                }
                if (running != null)
                {
                    ActiveTask = running;
                    SelectedTask = running;
                    running.IsActive = true;
                }

                // Initial percentage update
                UpdateTaskPercentages();

                Log.Information($"Loaded {Tasks.Count} tasks into UI");
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to load tasks";
                Log.Error(ex, "Error initializing MainViewModel");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Adds a new task with the given name.
        /// </summary>
        private async Task AddTaskAsync(string taskName)
        {
            try
            {
                var newTask = await _taskService.CreateTaskAsync(taskName);

                // Set order to place at the end
                newTask.Order = Tasks.Count;
                await _taskService.UpdateTaskAsync(newTask);

                var viewModel = new TaskViewModel(newTask, _taskService);
                viewModel.OnTaskDeleted += (sender, id) => OnTaskDeleted(id);
                Tasks.Add(viewModel);

                // Update percentages with new task
                UpdateTaskPercentages();

                // Don't auto-select new task, let user click it
                Log.Information($"Task added: {newTask.Name}");
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to add task";
                Log.Error(ex, "Error adding task");
            }
        }

        /// <summary>
        /// Selects a task and starts/pauses its timer appropriately.
        /// </summary>
        private async Task SelectTaskAsync(TaskViewModel taskVm)
        {
            try
            {
                // If same task is clicked, toggle its active state (pause/resume)
                if (ActiveTask == taskVm)
                {
                    if (taskVm.IsRunning)
                    {
                        // Pause the task
                        await PauseTaskAsync(taskVm);
                    }
                    else
                    {
                        // Resume the task
                        await StartTaskAsync(taskVm);
                    }
                }
                else
                {
                    // Different task is clicked
                    // Pause previous active task
                    if (ActiveTask != null)
                    {
                        await PauseTaskAsync(ActiveTask);
                    }

                    // Set new active task
                    SelectedTask = taskVm;
                    ActiveTask = taskVm;
                    await StartTaskAsync(ActiveTask);
                }

                Log.Debug($"Task selected: {taskVm.Name}");
                return;
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to select task";
                Log.Error(ex, "Error selecting task");
                return;
            }
        }

        public async Task StartTaskAsync(TaskViewModel taskVm)
        {
            foreach (var task in Tasks.Where(t => t != taskVm && t.IsRunning).ToList())
            {
                await _taskService.PauseTaskAsync(task.Id);
                task.IsRunning = false;
                task.IsActive = false;
            }
            await _taskService.StartTaskAsync(taskVm.Id);
            taskVm.IsRunning = true;
            taskVm.IsActive = true;
            ActiveTask = taskVm;
            SelectedTask = taskVm;
        }

        public async Task PauseTaskAsync(TaskViewModel taskVm)
        {
            await _taskService.PauseTaskAsync(taskVm.Id);
            taskVm.IsRunning = false;
            taskVm.IsActive = false;
            if (ActiveTask == taskVm) ActiveTask = null;
        }

        /// <summary>
        /// Removes a task from the collection when it's deleted.
        /// </summary>
        private void OnTaskDeleted(Guid taskId)
        {
            var taskVm = Tasks.FirstOrDefault(t => t.Id == taskId);
            if (taskVm != null)
            {
                Tasks.Remove(taskVm);
                if (ActiveTask?.Id == taskId)
                {
                    ActiveTask = null;
                }
                Log.Information($"Task removed from UI: {taskId}");
            }
        }

        /// <summary>
        /// Initializes the UI update timer (1 second interval).
        /// </summary>
        private void InitializeTimer()
        {
            _timerUpdate = new DispatcherTimer();
            _timerUpdate.Interval = TimeSpan.FromSeconds(1);
            _timerUpdate.Tick += (sender, e) => UpdateActiveTaskTimer();
            _timerUpdate.Start();
        }

        /// <summary>
        /// Updates the active task's elapsed time display and recalculates percentages.
        /// </summary>
        private void UpdateActiveTaskTimer()
        {
            if (ActiveTask?.IsRunning == true)
            {
                var now = DateTime.UtcNow;
                var since = ActiveTask.GetModel().RunningSinceUtc ?? now;
                var currentElapsed = ActiveTask.GetModel().ElapsedSeconds + Math.Max(0, (long)(now - since).TotalSeconds);
                ActiveTask.UpdateElapsedDisplay(currentElapsed);
                ActiveTask.GetModel().ElapsedSeconds = currentElapsed;
                ActiveTask.GetModel().RunningSinceUtc = now;
                _ = _taskService.UpdateTaskAsync(ActiveTask.GetModel());
            }

            // Recalculate and update percentages for all tasks
            UpdateTaskPercentages();
        }

        /// <summary>
        /// Calculates total elapsed time and updates percentage for each task.
        /// </summary>
        private void UpdateTaskPercentages()
        {
            long totalElapsedSeconds = Tasks.Sum(t => t.ElapsedSeconds);

            foreach (var task in Tasks)
            {
                task.SetTotalElapsedSeconds(totalElapsedSeconds);
            }
        }

        /// <summary>
        /// Sets a task's elapsed time to an absolute value and updates percentages.
        /// Used by the Change Time flyout for manual time adjustment.
        /// </summary>
        public void SetTaskElapsedTime(TaskViewModel taskVm, long totalSeconds)
        {
            taskVm.ElapsedSeconds = Math.Max(0, totalSeconds);
            UpdateTaskPercentages();
        }

        /// <summary>
        /// Updates the order of all tasks in the database based on their current position in the collection.
        /// Called after drag-and-drop reordering.
        /// </summary>
        public async Task UpdateTaskOrdersAsync()
        {
            try
            {
                for (int i = 0; i < Tasks.Count; i++)
                {
                    var task = Tasks[i];
                    var model = task.GetModel();

                    // Update order property
                    model.Order = i;
                    model.ModifiedAt = DateTime.UtcNow;

                    // Save to database
                    await _taskService.UpdateTaskAsync(model);
                }

                Log.Information("Task orders updated successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error updating task orders");
            }
        }

        /// <summary>
        /// Cleanup method for disposal.
        /// </summary>
        public void Dispose()
        {
            _timerUpdate?.Stop();
            _timerUpdate = null;
        }
    }
}

