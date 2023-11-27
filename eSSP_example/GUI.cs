using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MQTTnet.Server;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using MQTTnet.Client.Subscribing;
using System.Timers;
using MQTTnet.Client.Disconnecting;

namespace eSSP_example
{
    enum TipoDeError
    {
        NO_ERROR,
        BILLETE_NO_PERMITIDO,
        NO_HAY_VUELTO,
        BILLETE_FALSO,
        CAJA_FUERTE_LLENA
    }

    public partial class GUI : Form
    {
        CNV11 NV12; // the class used to interface with the validator
        private bool Running = false;
        private bool newTransaction;
        private decimal dineroRecibido;
        private decimal dineroPedido;
        int reconnectionAttempts = 5;
        private int dineroAlmacenado;
        private IMqttClient MQTTClient;
        string[] topicos = {"Payment/Proceso/VueltoRequerido/", "Payment/Proceso/PrecioServicio/", "Payment/Almacen/", "Payment/Proceso/Continuar/" };
        private string transTopicStatus = "Payment/Proceso/Transaccion/";

        public static GUI guiVar;
        private bool flagVuelto;

        private bool devolucionDineroPorInactividad;

        System.Timers.Timer timerUsuarioInactivo = new System.Timers.Timer();

        private decimal ultimoBilleteIngresado = 0;

        StatusNV11 nv11Parameters;
        public GUI()
        {
            InitializeComponent();
            guiVar = this;
        }

        private void GUI_Load(object sender, EventArgs e)
        {
            NV12 = new CNV11();
        }

        private void GUI_Shown(object sender, EventArgs e)
        {
            MainLoop();
        }

        void MainLoop()
        {
            NV12.CommandStructure.ComPort = Global.ComPort;
            NV12.CommandStructure.SSPAddress = Global.SSPAddress;
            NV12.CommandStructure.Timeout = 1500;

            nv11Parameters = new StatusNV11();

            _ = subscribeEvents();

            timerUsuarioInactivo.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            timerUsuarioInactivo.Interval = 15000;

            // Connect to validator
            if (ConnectToValidator(reconnectionAttempts))
            {
                Running = true;
            }

            //RUTEANDO LOS BILLETES DE 10 USD AL ALMACEN
            NV12.EnablePayout();
            NV12.ChangeNoteRoute(1000, "USD".ToCharArray(), false, textBox3);     //1000 = 10
            NV12.DisablePayout();

            while (Running)
            {
                while (newTransaction)
                {
                    dineroAlmacenado = NV12.CheckForStoredNotes();          //REFRESCAR LA VARIABLE DE VUELTO DISPONIBLE

                    KeyValuePair<bool, StatusNV11> temp = NV12.DoPoll(dineroRecibido, dineroAlmacenado, dineroPedido, textBox3);

                    nv11Parameters = temp.Value;

                    dineroRecibido = nv11Parameters.valorAcumulado;

                    while (temp.Key == false)
                    {
                        Console.WriteLine("PTMRE FALLO EL PUERTO COM");
                        while (true)
                        {
                            // attempt reconnect, pass over number of reconnection attempts
                            if (ConnectToValidator(reconnectionAttempts) == true)
                            {
                                textBox3.AppendText("Reconexion al puerto COM\r\n");
                                break; // if connection successful, break out and carry on
                                // if not successful, stop the execution of the poll loop
                            }
                            textBox3.AppendText("Cerrando puerto COM\r\n");
                            NV12.SSPComms.CloseComPort(); // close com port before return
                            //return;
                        }
                        textBox3.AppendText("Billetero reconectado\r\n");
                        break;
                    }

                    if(nv11Parameters.tipoError == (int)TipoDeError.BILLETE_NO_PERMITIDO)
                    {
                        string msg = "Billete no permitido - " + nv11Parameters.valorBillete.ToString();
                        _ = sendPayload("Payment/Situacion/", "Billete no permitido");
                        _ = sendPayload("Payment/Error/", msg);
                    }

                    else if(nv11Parameters.tipoError == (int)TipoDeError.NO_HAY_VUELTO)
                    {
                        _ = sendPayload("Payment/Situacion/", "No hay vuelto");
                        _ = sendPayload("Payment/Error/", "No vuelto");
                    }

                    else if(nv11Parameters.tipoError == (int)TipoDeError.BILLETE_FALSO)
                    {
                        string msg2 = "Billete falso - " + nv11Parameters.valorBillete.ToString();
                        _ = sendPayload("Payment/Error/", msg2);
                        _ = sendPayload("Payment/Situacion/", "Billete falso");
                    }
                    
                    else if(nv11Parameters.tipoError == (int)TipoDeError.CAJA_FUERTE_LLENA)
                    {
                        _ = sendPayload("Payment/Situacion/", "Caja fuerte llena");
                        _ = sendPayload("Payment/Error/", "Caja fuerte llena");
                    }
                    else
                    {
                        //dineroRecibido = nv11Parameters.valorAcumulado;

                        textBox2.Text = dineroRecibido.ToString();

                        // Si el sistema de pago retorna false, significa que no hay nada que hacer con el sistema de pago, y el proceso sigue
                        if (paymentSystem() == true)
                        {
                            DeshabilitarBilletero();
                        }
                    }
                    

                    timer1.Enabled = true;

                    while (timer1.Enabled)
                    {
                        Application.DoEvents();
                        Thread.Sleep(1); // Yield to free up CPU
                    }
                }

                if (devolucionDineroPorInactividad == true)
                {

                    if (dineroRecibido > 0)
                    {
                        NV12.EnableValidator();
                        NV12.EnablePayout();

                        Console.WriteLine("Se tiene que devolver " + dineroRecibido);

                        decimal dineroParaDevolver = dineroRecibido / 10;

                        if (dineroAlmacenado >= dineroParaDevolver)
                        {
                            for (int i = 0; i < dineroParaDevolver; i++)
                            {
                                if (guiVar.NV12.PayoutNextNote(textBox3) == false)
                                {
                                    i--;
                                }
                                NV12.DoPoll(0, 0, 0, textBox3);
                                NV12.EnableValidator();
                                NV12.EnablePayout();
                            }
                        }
                        else
                        {
                            _ = sendPayload("Payment/Situacion/", "staff devolvera el dinero");
                            decimal temp2 = dineroRecibido;
                            _ = sendPayload("Payment/Staff/Cantidad/", temp2.ToString());
                        }

                    }

                    DeshabilitarBilletero();
                    _ = sendPayload(guiVar.transTopicStatus, "Transaccion cancelada\r\n");
                    _ = sendPayload("Payment/Proceso/Inicio/", "false");                    //BANDERA QUE INDICA LA FINALIZACION DE UNA TRANSACCION

                    devolucionDineroPorInactividad = false;
                }

                timer1.Enabled = true;

                while (timer1.Enabled)
                {
                    Application.DoEvents();
                    Thread.Sleep(1); // Yield to free up CPU
                }
            }

            //close com port
            NV12.SSPComms.CloseComPort();
        }
        private bool ConnectToValidator(int attempts)
        {
            // setup timer
            //System.Windows.Forms.Timer reconnectionTimer = new System.Windows.Forms.Timer();
            //reconnectionTimer.Tick += new EventHandler(reconnectionTimer_Tick);
            //reconnectionTimer.Interval = 3000; // ms

            // run for number of attempts specified
            for (int i = 0; i < attempts; i++)
            {
                // close com port in case it was open
                NV12.SSPComms.CloseComPort();

                // turn encryption off for first stage
                NV12.CommandStructure.EncryptionStatus = false;

                // if the key negotiation is successful then set the rest up
                if (NV12.OpenComPort() && NV12.NegotiateKeys())
                {
                    NV12.CommandStructure.EncryptionStatus = true; // now encrypting
                    // find the max protocol version this validator supports
                    byte maxPVersion = FindMaxProtocolVersion();
                    if (maxPVersion >= 6)
                    {
                        NV12.SetProtocolVersion(maxPVersion);
                    }
                    else
                    {
                        //MessageBox.Show("Este programa no soporta validadores con protocolos menores al V6", "ERROR");
                        return false;
                    }
                    // get info from the validator and store useful vars
                    NV12.ValidatorSetupRequest();
                    // inhibits, this sets which channels can receive notes
                    NV12.SetInhibits();
                    // check for notes already in the float on startup
                    dineroAlmacenado = NV12.CheckForStoredNotes();
                    // enable, this allows the validator to operate
                    //_ = sendPayload("Payment/Almacen/Cantidad", dineroAlmacenado.ToString());
                    //NV12.EnableValidator(textBox1); // Se comentó esta linea con el propósito de habilitar la recepción de billetes mas tarde. Observar función MainLoop()

                    // value reporting, set whether the validator reports channel or coin value in 
                    // subsequent requests
                    NV12.SetValueReportingType(false);

                    return true;
                }
                // reset timer
                //reconnectionTimer.Enabled = true;
                //while (reconnectionTimer.Enabled) Application.DoEvents();
            }
            return false;
        }

        /*private void reconnectionTimer_Tick(object sender, EventArgs e)
        {
            if (sender is System.Windows.Forms.Timer)
            {
                System.Windows.Forms.Timer t = sender as System.Windows.Forms.Timer;
                t.Enabled = false;
            }
        }*/

        private byte FindMaxProtocolVersion()
        {
            // not dealing with protocol under level 6
            // attempt to set in validator
            byte b = 0x06;
            while (true)
            {
                NV12.SetProtocolVersion(b);
                if (NV12.CommandStructure.ResponseData[0] == CCommands.SSP_RESPONSE_FAIL)
                    return --b;
                b++;
                if (b > 20) return 0x06; // return lowest if p version runs too high
            }
        }

        public bool paymentSystem()
        {
            if (nv11Parameters.nuevoBillete)
            {
                Console.WriteLine("Nuevo Billete");
                ReiniciarTimerUsuario();

                //ultimoBilleteIngresado = nv11Parameters.valorBillete;

                if (nv11Parameters.valorAcumulado == 0)
                {
                    Console.WriteLine("No ingresa todavia ni un billete");
                    return false;
                }

                if(nv11Parameters.valorBillete > 0)
                {
                    _ = sendPayload("Payment/Billete/", nv11Parameters.valorBillete.ToString());
                }

                if (nv11Parameters.valorAcumulado >= dineroPedido)
                {
                    decimal dineroParaDevolver = (nv11Parameters.valorAcumulado - dineroPedido) / 10;

                    if (dineroParaDevolver != 0)
                    {
                        if (dineroAlmacenado >= dineroParaDevolver)
                        {
                            for (int i = 0; i < dineroParaDevolver; i++)
                            {
                                //Si devuelve false es que no devolvio el billete y por ende se le debe restar una iteracion al loop
                                //En caso contrario, retornara true y devolvera un billete
                                if (NV12.PayoutNextNote() == false)
                                {
                                    i--;
                                }
                                NV12.DoPoll(0, 0, 0, textBox3);
                                NV12.EnableValidator();
                            }

                            //Transaccion exitosa
                            _ = sendPayload(transTopicStatus, "Transaccion exitosa\r\n");
                            _ = sendPayload("Payment/Proceso/Inicio/", "false");             //FLAG QUE INDICA LA FINALIZACION DE UNA TRANSACCION
                            return true;
                        }
                        else
                        {
                            //IMPLEMENTAR EL ENVIO POR MQTT INDICANDO QUE EL STAFF LE DEVOLVERA EL DINERO
                            //IMPLEMENTAR EL ENVIO POR  MQTT INDICANDO CUANTO DEBEN DEVOLVER DE DINERO AL STAFF
                        }
                    }
                    else
                    {
                        //TRANSACCION EXITOSA SIN DAR VUELTO
                        _ = sendPayload(transTopicStatus, "Transaccion exitosa\r\n");
                        _ = sendPayload("Payment/Proceso/Inicio/", "false");
                        return true;
                    }
                }
            }
            return false;
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            timer1.Enabled = false;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (dineroRecibido > 0)
            {
                Console.WriteLine("Se tiene que devolver " + dineroRecibido.ToString());
                
                decimal dineroParaDevolver = dineroRecibido / 10;

                if (dineroAlmacenado >= dineroParaDevolver)
                {
                    for (int i = 0; i < dineroParaDevolver; i++)
                    {
                        if (NV12.PayoutNextNote() == false)
                        {
                            i--;
                        }
                        NV12.DoPoll(0, 0, 0, textBox3);
                        NV12.EnableValidator();
                    }
                }
                else
                {
                    _ = sendPayload("Payment/Situacion/", "staff devolvera el dinero");
                    _ = sendPayload("Payment/Staff/Cantidad/", dineroRecibido.ToString());
                }
                
            }

            DeshabilitarBilletero();
            _ = sendPayload(transTopicStatus, "Transaccion cancelada\r\n");
            _ = sendPayload("Payment/Proceso/Inicio/", "false");
        }

        static async Task subscribeEvents()
        {
            var mqttFactory = new MqttFactory();

            guiVar.MQTTClient = mqttFactory.CreateMqttClient();

            var mqttClientOptions = new MqttClientOptionsBuilder()
                        .WithTcpServer("100.26.219.8", 1883)
                        .WithCredentials("billeteronv11", "billeterownr123")
                        .Build();

            guiVar.MQTTClient.UseConnectedHandler(async e =>
            {
                Console.WriteLine("Conectado al Broker!");
                foreach (string topico in guiVar.topicos)
                {
                    var topicFilter = new MqttClientSubscribeOptionsBuilder()
                                        .WithTopicFilter(topico)     //Cambiar por el tipo de tópico para subcribirse al valor de billetes que la aplicación envía al billetero para pagar
                                        .Build();

                    await guiVar.MQTTClient.SubscribeAsync(topicFilter);
                }

                int temp = guiVar.NV12.CheckForStoredNotes();

                _ = sendPayload("Payment/Almacen/Cantidad/", temp.ToString());
            });

            guiVar.MQTTClient.UseDisconnectedHandler(async e =>
            {
                guiVar.NV12.DisablePayout();
                guiVar.NV12.DisableValidator();
                guiVar.timerUsuarioInactivo.Stop();

                //REINICIA LAS VARIABLES
                guiVar.dineroRecibido = 0;
                guiVar.dineroPedido = 0;

                guiVar.SetText("0", guiVar.textBox1);
                guiVar.SetText("0", guiVar.textBox2);

                //SE APAGA EL BILLETERO
                guiVar.newTransaction = false;

                Console.WriteLine("MQTT reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(5));
                await guiVar.MQTTClient.ConnectAsync(mqttClientOptions, CancellationToken.None);
            });

            guiVar.MQTTClient.UseApplicationMessageReceivedHandler(async e =>
            {
                var topico = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (topico == "Payment/Proceso/VueltoRequerido/")
                {
                    var almacenCantidad = guiVar.NV12.CheckForStoredNotes();
                    //SE MULTIPLICA *10 PORQUE CHECKFORSTOREDNOTES DEVUELVE LA CANTIDAD DE BILLETES QUE HAY
                    //Y COMO SOLO SE GUARDA BILLETES DE 10USD
                    almacenCantidad *= 10;                                      
                    int temp = int.Parse(payload);

                    if (almacenCantidad >= temp)
                    {
                        _ = sendPayload("Payment/Proceso/VueltoSuficiente/", "true");
                        guiVar.flagVuelto = true;
                        Console.WriteLine("Si hay vuelto");
                    }
                    else
                    {
                        _ = sendPayload("Payment/Proceso/VueltoSuficiente/", "false");
                        guiVar.flagVuelto = false;
                        Console.WriteLine("No hay vuelto");
                    }
                }
                else if (topico == "Payment/Proceso/PrecioServicio/")
                {
                    if (guiVar.flagVuelto)
                    {
                        int temp = int.Parse(payload);
                        guiVar.flagVuelto = false;
                        if (temp > 0)
                        {
                            guiVar.newTransaction = true;
                            guiVar.dineroPedido = temp;
                            guiVar.SetText(temp.ToString(), guiVar.textBox1);
                            Console.WriteLine("Precio del servicio es " + temp.ToString());

                            //ACTIVO BILLETERO
                            guiVar.NV12.EnableValidator();
                            guiVar.NV12.EnablePayout();

                            //ACTIVO TIMER DE INACTIVIDAD
                            guiVar.timerUsuarioInactivo.Enabled = true;

                            //ACK DE AFIRMACION DE INICIO DE TRANSACCION
                            _ = sendPayload("Payment/Proceso/Inicio/", "true");
                        }
                        else
                        {
                            //MANDAR ERROR EN TOPIC DE ERROR DICIENDO LLEGO VALOR 0 O NEGATIVO COMO PAGO
                        }
                    }
                    else
                    {
                        //MANDAR ERROR DE QUE SE SALTEO UN PROCESO, VER EL "COMO" PORQUE NO DEBERIA PASAR
                    }
                }
                else if(topico == "Payment/Almacen/")
                {
                    if(payload == "true")
                    {
                        var temp = guiVar.NV12.CheckForStoredNotes();
                        _ = sendPayload("Payment/Almacen/Cantidad/", temp.ToString());
                    }
                    else
                    {
                        //MANDAR ERROR QUE EL PAYLOAD MANDO ALGO DIFERENTE A TRUE QUE ES IMPOSIBLE
                    }
                }
                else if(topico == "Payment/Proceso/Continuar/")
                {
                    if (payload == "true")
                    {
                        guiVar.newTransaction = true;
                        guiVar.NV12.EnableValidator();
                        guiVar.NV12.EnablePayout();
                        guiVar.timerUsuarioInactivo.Start();
                    }
                    else if (payload == "false")
                    {
                        guiVar.devolucionDineroPorInactividad = true;
                    }
                }
            });

            await guiVar.MQTTClient.ConnectAsync(mqttClientOptions, CancellationToken.None);
        }

        static async Task sendPayload(string topico, string payload)
        {
            var mqttMsg = new MqttApplicationMessageBuilder()
                        .WithTopic(topico)
                        .WithPayload(payload)
                        .Build();
            await guiVar.MQTTClient.PublishAsync(mqttMsg);
        }

        static public void DeshabilitarBilletero()
        {
            //APAGAR TIMER
            guiVar.timerUsuarioInactivo.Stop();

            //REINICIA LAS VARIABLES
            guiVar.dineroRecibido = 0;
            guiVar.dineroPedido = 0;
            guiVar.textBox2.Text = "0";
            guiVar.textBox1.Text = "0";

            //SE APAGA EL BILLETERO
            guiVar.newTransaction = false;
            guiVar.NV12.DisableValidator();
            guiVar.NV12.DisablePayout();
            Console.WriteLine("Time before wait: " + DateTime.Now.ToString());
            //SE UPDATEA LA CANTIDAD DE BILLETES QUE HAY PARA DAR VUELTO
            Thread.Sleep(5000);
            Console.WriteLine("Time after wait: " + DateTime.Now.ToString());
            int temp = guiVar.NV12.CheckForStoredNotes();
            _ = sendPayload("Payment/Almacen/Cantidad/", temp.ToString());
        }

        delegate void SetTextCallback(string text, TextBox textBox);

        private void SetText(string text, TextBox textBox)
        {
            if (textBox.InvokeRequired)
            {
                SetTextCallback d = new SetTextCallback(SetText);
                this.Invoke(d, new object[] { text, textBox });
            }
            else
            {
                textBox.Text = text;
            }
        }

        private void timer1_Tick_1(object sender, EventArgs e)
        {
            timer1.Enabled = false;
        }

        private void ReiniciarTimerUsuario()
        {
            guiVar.timerUsuarioInactivo.Stop();
            guiVar.timerUsuarioInactivo.Start();
        }
        private static void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            guiVar.newTransaction = false;
            guiVar.NV12.DisableValidator();
            guiVar.NV12.DisablePayout();
            guiVar.timerUsuarioInactivo.Stop();
            _ = sendPayload("Payment/Situacion/", "Inactividad");
        }
    }
}
