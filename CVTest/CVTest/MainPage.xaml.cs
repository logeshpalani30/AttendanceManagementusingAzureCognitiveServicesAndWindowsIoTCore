using CustomVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.AI.MachineLearning;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace CVTest
{
    public sealed partial class MainPage : Page, INotifyPropertyChanged
    {
        MediaCapture mediaCapture;

        private bool isPreviewing;

        private DisplayRequest displayRequest = new DisplayRequest();

        private modelModel model;

        private MediaFrameReader frameReader;

        private int processingFlag;

        public string cognitiveServicesKey = "99764";

        private string cognitiveServicesUrl = "https://centralindia.api.cognitive.microsoft.com/";

        private static readonly float[] Anchors = new float[]
            {0.573f, 0.677f, 1.87f, 2.06f, 3.34f, 5.47f, 7.88f, 3.53f, 9.77f, 9.17f};

        List<string> labels = new List<string>()
        {
            "IdCard", "NotIdCard"
        };

        private readonly int maxDetections = 20;
        private readonly float probabilityThreshold = 0.1f;
        private readonly float iouThreshold = 0.45f;

        private Brush frameColor = new SolidColorBrush(Colors.Red);

        public Brush FrameColor
        {
            get => this.frameColor;
            set => this.SetProperty(ref this.frameColor, value);
        }

        private string score;

        public string Score
        {
            get => this.score;
            set => this.SetProperty(ref this.score, value);
        }

        private string loginMessage = "Recognization Started";

        public string LoginMessage
        {
            get => this.loginMessage;
            set => this.SetProperty(ref this.loginMessage, value);
        }

        private ImageSource recogImageSource;
        private ComputerVisionClient client;

        public ImageSource RecogImageSource
        {
            get => this.recogImageSource;
            set => this.SetProperty(ref this.recogImageSource, value);
        }

        private readonly SpeechSynthesizer speechSynthesizer;

        private readonly MediaPlayer mediaPlayer;

        public MainPage()
        {
            this.InitializeComponent();

            Application.Current.Suspending += Current_Suspending;
            this.Loaded += MainPage_Loaded;

            speechSynthesizer = CreateSpeechSynthesizer();
            mediaPlayer = new MediaPlayer();

            client = new ComputerVisionClient(new ApiKeyServiceClientCredentials(cognitiveServicesKey))
            {
                Endpoint = cognitiveServicesUrl
            };
        }

        private SpeechSynthesizer CreateSpeechSynthesizer()
        {
            var synthesizer = new SpeechSynthesizer();
            var voice = SpeechSynthesizer.AllVoices.SingleOrDefault(i => i.Gender == VoiceGender.Female) ??
                        SpeechSynthesizer.DefaultVoice;

            synthesizer.Voice = voice;
            return synthesizer;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            var modelFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///Assets/model.onnx"));
            model = await modelModel.CreateFromStreamAsync(modelFile);

            await StartPreviewAsync();
        }

        private async void Current_Suspending(object sender, SuspendingEventArgs e)
        {
            try
            {
                if (Frame.CurrentSourcePageType == typeof(MainPage))
                {
                    var defferal = e.SuspendingOperation.GetDeferral();
                    await CleanupCameraAsync();
                    defferal.Complete();
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine(exception);
            }
        }

        private async Task CleanupCameraAsync()
        {
            if (mediaCapture != null)
            {
                if (isPreviewing)
                {
                    await mediaCapture.StopPreviewAsync();
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    VideoPreview.Source = null;

                    displayRequest?.RequestRelease();

                    mediaCapture.Dispose();
                    mediaCapture = null;
                });
            }
        }

        private async Task StartPreviewAsync()
        {
            try
            {
                var cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Back);
                var settings = new MediaCaptureInitializationSettings() {VideoDeviceId = cameraDevice.Id};

                mediaCapture = new MediaCapture();
                await mediaCapture.InitializeAsync(settings);
                displayRequest.RequestActive();
                DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;

                var frameSource = this.mediaCapture.FrameSources
                    .Where(source => source.Value.Info.SourceKind == MediaFrameSourceKind.Color).First();

                this.frameReader = await this.mediaCapture.CreateFrameReaderAsync(frameSource.Value);

                this.frameReader.FrameArrived += FrameReader_FrameArrivedAsync;

                await this.frameReader.StartAsync();
            }
            catch (UnauthorizedAccessException e)
            {
                ContentDialog unauthorizedMsg = new ContentDialog()
                {
                    Title = "No Camera Access",
                    Content = "The app was denied access to the camera",
                    CloseButtonText = "OK"
                };
                await unauthorizedMsg.ShowAsync();
                isPreviewing = true;
                return;
            }

            try
            {
                VideoPreview.Source = mediaCapture;
                await mediaCapture.StartPreviewAsync();
                isPreviewing = true;
            }
            catch (FileLoadException e)
            {
                mediaCapture.CaptureDeviceExclusiveControlStatusChanged +=
                    MediaCapture_CaptureDeviceExclusiveControlStatusChanged;
            }
        }

        private async void MediaCapture_CaptureDeviceExclusiveControlStatusChanged(MediaCapture sender,
            MediaCaptureDeviceExclusiveControlStatusChangedEventArgs args)
        {
            if (args.Status == MediaCaptureDeviceExclusiveControlStatus.SharedReadOnlyAvailable)
            {
                var unauthorizedMsg = new ContentDialog()
                {
                    Title = "No Camera Access",
                    Content = "The app was denied access to the camera",
                    CloseButtonText = "OK"
                };
            }
            else if (args.Status == MediaCaptureDeviceExclusiveControlStatus.ExclusiveControlAvailable && !isPreviewing)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => { await StartPreviewAsync(); });
            }
        }

        private async void FrameReader_FrameArrivedAsync(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (Interlocked.CompareExchange(ref this.processingFlag, 1, 0) == 0)
            {
                try
                {
                    using (var acquiredFrame = sender.TryAcquireLatestFrame())
                    using (var videoFrame = acquiredFrame.VideoMediaFrame?.GetVideoFrame())
                    {
                        if (videoFrame != null)
                        {
                            var input = new modelInput
                            {
                                data = ImageFeatureValue.CreateFromVideoFrame(videoFrame)
                            };

                            var evalOutput = await model.EvaluateAsync(input);
                            var resultModels = this.ProcessOutputAsync(evalOutput);

                            if (resultModels.Any())
                            {
                                await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                    () => { Score = resultModels[0].Probability.ToString("#0.00"); });

                                if (resultModels[0].Probability * 100 > 40 && resultModels[0].TagName == labels[0])
                                {
                                    await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                                    {
                                        FrameColor = new SolidColorBrush(Colors.LimeGreen);
                                        Score = resultModels[0].Probability.ToString("#0.00");
                                    });

                                    var previewProperties =
                                        mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType
                                            .VideoPreview) as VideoEncodingProperties;

                                    var currentMediaFrame = new VideoFrame(BitmapPixelFormat.Bgra8,
                                        (int) previewProperties.Width, (int) previewProperties.Height);

                                    using (var currentFrame =
                                        await mediaCapture.GetPreviewFrameAsync(currentMediaFrame))
                                    {
                                        var previewFrame = currentFrame.SoftwareBitmap;

                                        await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                                        {
                                            var data = await EncodedBytes(previewFrame, BitmapEncoder.JpegEncoderId);
                                            Stream stream = new MemoryStream(data);

                                            var apiResult = client.RecognizePrintedTextInStreamAsync(false, stream);

                                            apiResult.Wait();

                                            var ocrResult = apiResult.Result;

                                            if (ocrResult.Regions.Any())
                                            {
                                                foreach (var r in ocrResult.Regions)
                                                {
                                                    if (r.Lines.Count > 2)
                                                    {
                                                        var employeeName = r.Lines[1].Words[0].Text;
                                                        var employeeId = r.Lines[2].Words[0].Text;
                                                        var message =
                                                            $"Hey {employeeName} your logged in with {employeeId}";

                                                        // Do API calls here, then based on response show or play some users to show user
                                                        using (var speechStream =
                                                            await speechSynthesizer
                                                                .SynthesizeTextToStreamAsync(message))
                                                        {
                                                            mediaPlayer.Source =
                                                                MediaSource.CreateFromStream(speechStream,
                                                                    speechStream.ContentType);
                                                        }

                                                        await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                                            () => { LoginMessage = message; });

                                                        mediaPlayer.Play();
                                                    }
                                                    else
                                                    {
                                                        using (var speechStream =
                                                            await speechSynthesizer.SynthesizeTextToStreamAsync(
                                                                $"Hey dude access Id not found cloud show me correctly")
                                                        )
                                                        {
                                                            mediaPlayer.Source =
                                                                MediaSource.CreateFromStream(speechStream,
                                                                    speechStream.ContentType);
                                                        }

                                                        mediaPlayer.Play();
                                                    }
                                                }
                                            }
                                        });
                                    }
                                }
                            }

                            this.processingFlag = 0;
                            await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                () =>
                                {
                                    FrameColor = new SolidColorBrush(Colors.Red);
                                    LoginMessage = "";
                                });
                        }
                    }
                }
                catch (Exception)
                {
                    this.processingFlag = 0;
                    await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                        () =>
                        {
                            FrameColor = new SolidColorBrush(Colors.Red);
                            LoginMessage = "";
                        });
                }
            }
        }

        private async Task<byte[]> EncodedBytes(SoftwareBitmap soft, Guid encoderId)
        {
            byte[] array = null;

            using (var ms = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, ms);
                encoder.SetSoftwareBitmap(soft);

                try
                {
                    await encoder.FlushAsync();
                }
                catch (Exception ex)
                {
                    return new byte[0];
                }

                array = new byte[ms.Size];
                await ms.ReadAsync(array.AsBuffer(), (uint) ms.Size, InputStreamOptions.None);
            }

            return array;
        }

        private class ExtractedBoxes
        {
            public List<BoundingBox> Boxes { get; }
            public List<float[]> Probabilities { get; }

            public ExtractedBoxes(List<BoundingBox> boxes, List<float[]> probs)
            {
                this.Boxes = boxes;
                this.Probabilities = probs;
            }
        }

        private List<PredictionModel> ProcessOutputAsync(modelOutput evalOutput)
        {
            var label = evalOutput.model_outputs0;
            var extractedBoxes = this.ExtractBoxes(evalOutput.model_outputs0, MainPage.Anchors);
            return this.SuppressNonMaximum(extractedBoxes);
        }

        private List<PredictionModel> SuppressNonMaximum(MainPage.ExtractedBoxes extractedBoxes)
        {
            var predictions = new List<PredictionModel>();

            if (extractedBoxes.Probabilities.Count > 0)
            {
                var maxProbs = extractedBoxes.Probabilities.Select(x => x.Max()).ToArray();

                while (predictions.Count < this.maxDetections)
                {
                    var max = maxProbs.Max();
                    if (max < this.probabilityThreshold)
                    {
                        break;
                    }

                    var index = Array.IndexOf(maxProbs, max);
                    var maxClass = Array.IndexOf(extractedBoxes.Probabilities[index], max);

                    predictions.Add(new PredictionModel(max, this.labels[maxClass], extractedBoxes.Boxes[index]));

                    for (int i = 0; i < extractedBoxes.Boxes.Count; i++)
                    {
                        if (CalculateIOU(extractedBoxes.Boxes[index], extractedBoxes.Boxes[i]) > this.iouThreshold)
                        {
                            extractedBoxes.Probabilities[i][maxClass] = 0;
                            maxProbs[i] = extractedBoxes.Probabilities[i].Max();
                        }
                    }
                }
            }

            return predictions;
        }

        private static float CalculateIOU(BoundingBox box0, BoundingBox box1)
        {
            var x1 = Math.Max(box0.Left, box1.Left);
            var y1 = Math.Max(box0.Top, box1.Top);
            var x2 = Math.Min(box0.Left + box0.Width, box1.Left + box1.Width);
            var y2 = Math.Min(box0.Top + box0.Height, box1.Top + box1.Height);
            var w = Math.Max(0, x2 - x1);
            var h = Math.Max(0, y2 - y1);

            return w * h / ((box0.Width * box0.Height) + (box1.Width * box1.Height) - (w * h));
        }

        private ExtractedBoxes ExtractBoxes(TensorFloat predictionOutput, float[] anchors)
        {
            var shape = predictionOutput.Shape;
            Debug.Assert(shape.Count == 4, "The model output has unexpected shape");
            Debug.Assert(shape[0] == 1, "The batch size must be 1");

            IReadOnlyList<float> outputs = predictionOutput.GetAsVectorView();

            var numAnchor = anchors.Length / 2;
            var channels = shape[1];
            var height = shape[2];
            var width = shape[3];

            Debug.Assert(channels % numAnchor == 0);
            var numClass = (channels / numAnchor) - 5;

            Debug.Assert(numClass == this.labels.Count);

            var boxes = new List<BoundingBox>();
            var probs = new List<float[]>();
            for (int gridY = 0; gridY < height; gridY++)
            {
                for (int gridX = 0; gridX < width; gridX++)
                {
                    int offset = 0;
                    int stride = (int) (height * width);
                    int baseOffset = gridX + gridY * (int) width;

                    for (int i = 0; i < numAnchor; i++)
                    {
                        var x = (Logistic(outputs[baseOffset + (offset++ * stride)]) + gridX) / width;
                        var y = (Logistic(outputs[baseOffset + (offset++ * stride)]) + gridY) / height;
                        var w = (float) Math.Exp(outputs[baseOffset + (offset++ * stride)]) * anchors[i * 2] / width;
                        var h = (float) Math.Exp(outputs[baseOffset + (offset++ * stride)]) * anchors[i * 2 + 1] /
                                height;

                        x = x - (w / 2);
                        y = y - (h / 2);

                        var objectness = Logistic(outputs[baseOffset + (offset++ * stride)]);

                        var classProbabilities = new float[numClass];
                        for (int j = 0; j < numClass; j++)
                        {
                            classProbabilities[j] = outputs[baseOffset + (offset++ * stride)];
                        }

                        var max = classProbabilities.Max();
                        for (int j = 0; j < numClass; j++)
                        {
                            classProbabilities[j] = (float) Math.Exp(classProbabilities[j] - max);
                        }

                        var sum = classProbabilities.Sum();
                        for (int j = 0; j < numClass; j++)
                        {
                            classProbabilities[j] *= objectness / sum;
                        }

                        if (classProbabilities.Max() > this.probabilityThreshold)
                        {
                            boxes.Add(new BoundingBox(x, y, w, h));
                            probs.Add(classProbabilities);
                        }
                    }

                    Debug.Assert(offset == channels);
                }
            }

            Debug.Assert(boxes.Count == probs.Count);
            return new ExtractedBoxes(boxes, probs);
        }

        private static float Logistic(float x)
        {
            if (x > 0)
            {
                return (float) (1 / (1 + Math.Exp(-x)));
            }
            else
            {
                var e = Math.Exp(x);
                return (float) (e / (1 + e));
            }
        }

        private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(
            Windows.Devices.Enumeration.Panel desiredPanel)
        {
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x =>
                x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

            return desiredDevice ?? allVideoDevices.FirstOrDefault();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            storage = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}