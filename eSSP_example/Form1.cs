using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Server;
using MQTTnet;
using MQTTnet.Client;
using System.Runtime.InteropServices.ComTypes;
using MQTTnet.Protocol;
using MQTTnet.Client.Options;
using MQTTnet.Client.Subscribing;
using MQTTnet.Internal;

namespace eSSP_example
{
    public partial class Form1 : Form
    {
        // Variables used by this program.
        private bool Running = false;
        int pollTimer = 250; // timer in ms
        int reconnectionAttempts = 5;
        CNV11 NV11; // the class used to interface with the validator
        bool FormSetup = false;
        private bool modoInsertarBilletes;
        int transaccionesExitosas = 0;

        // Constructor
        public Form1()
        {
            InitializeComponent();
            timer1.Interval = pollTimer;
        }

        // This updates UI variables such as textboxes etc.
        void UpdateUI()
        {
            // update text boxes
            //totalAcceptedNumText.Text = NV11.NotesAccepted.ToString();
            totalAcceptedNumText.Text = transaccionesExitosas.ToString();
            totalNumNotesDispensedText.Text = NV11.NotesDispensed.ToString();
            notesInStorageText.Text = NV11.GetStorageInfo();
        }

        // This is a one off function that is called the first time the MainLoop()
        // function runs, it just sets up a few of the UI elements that only need
        // updating once.
        private void SetupFormLayout()
        {
            // need validator class instance
            if (NV11 == null)
            {
                MessageBox.Show("NV11 class is null.", "ERROR");
                return;
            }

            // find number and value of channels and update combo box
            noteToRecycleComboBox.Items.Add("No Recycling");

            foreach (ChannelData d in NV11.UnitDataList)
            {
                string s = d.Value / 100 + " " + d.Currency[0] + d.Currency[1] + d.Currency[2];
                noteToRecycleComboBox.Items.Add(s);
            }
            
            noteToRecycleComboBox.Items.Add("Show Routing"); 
            
                // start on first choice which will always be no recycling
            noteToRecycleComboBox.Text = noteToRecycleComboBox.Items[0].ToString();
        }

        // This function opens the com port and attempts to connect with the validator. It then negotiates
        // the keys for encryption and performs some other setup commands.
        private bool ConnectToValidator(int attempts)
        {
            // setup timer
            System.Windows.Forms.Timer reconnectionTimer = new System.Windows.Forms.Timer();
            reconnectionTimer.Tick += new EventHandler(reconnectionTimer_Tick);
            reconnectionTimer.Interval = 3000; // ms

            // run for number of attempts specified
            for (int i = 0; i < attempts; i++)
            {
                // close com port in case it was open
                NV11.SSPComms.CloseComPort();

                // turn encryption off for first stage
                NV11.CommandStructure.EncryptionStatus = false;

                // if the key negotiation is successful then set the rest up
                if (NV11.OpenComPort(textBox1) &&  NV11.NegotiateKeys(textBox1))
                {
                    NV11.CommandStructure.EncryptionStatus = true; // now encrypting
                    // find the max protocol version this validator supports
                    byte maxPVersion = FindMaxProtocolVersion();
                    if (maxPVersion >= 6)
                    {
                        NV11.SetProtocolVersion(maxPVersion, textBox1);
                    }
                    else
                    {
                        MessageBox.Show("Este programa no soporta validadores con protocolos menores al V6", "ERROR");
                        return false;
                    }
                    // get info from the validator and store useful vars
                    NV11.ValidatorSetupRequest(textBox1);
                    // inhibits, this sets which channels can receive notes
                    NV11.SetInhibits(textBox1);
                    // enable, this allows the validator to operate
                    //NV11.EnableValidator(textBox1); // Se comentó esta linea con el propósito de habilitar la recepción de billetes mas tarde. Observar función MainLoop()

                    // value reporting, set whether the validator reports channel or coin value in 
                    // subsequent requests
                    NV11.SetValueReportingType(false, textBox1);

                    return true;
                }
                // reset timer
                reconnectionTimer.Enabled = true;
                while (reconnectionTimer.Enabled) Application.DoEvents();
            }
            return false;
        }

        // This function finds the maximum protocol version that a validator supports. To do this
        // it attempts to set a protocol version starting at 6 in this case, and then increments the
        // version until error 0xF8 is returned from the validator which indicates that it has failed
        // to set it. The function then returns the version number one less than the failed version.
        private byte FindMaxProtocolVersion()
        {
            // not dealing with protocol under level 6
            // attempt to set in validator
            byte b = 0x06;
            while (true)
            {
                NV11.SetProtocolVersion(b);
                if (NV11.CommandStructure.ResponseData[0] == CCommands.SSP_RESPONSE_FAIL)
                    return --b;
                b++;
                if (b > 20) return 0x06; // return lowest if p version runs too high
            }
        }
        
        // Events handling section 
        private void Form1_Load(object sender, EventArgs e)
        {
            // create an instance of the validator info class
            NV11 = new CNV11();
            btnHalt.Enabled = false;


            if (Properties.Settings.Default.CommWindow)
            {
                //NV11.CommsLog.Show();
                //logTickBox.Checked = true;
            }
            //else
                //logTickBox.Checked = false;
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            // Position the comms window of the validator
            Point p = this.Location;
            p.X += this.Width;
            //NV11.CommsLog.Location = p;
            // hide this and show opening menu
            this.Hide();
            frmOpenMenu menu = new frmOpenMenu(this);
            menu.Show();
        }

        private void btnRun_Click(object sender, EventArgs e)
        {
            modoInsertarBilletes = false;
            NV11.DisableValidator();
            NV11.DisablePayout();
            NV11.SSPComms.CloseComPort();

            GUI gui = new GUI();
            gui.Show();
            this.Hide();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            base.Dispose();
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Running = false;
            Form formSettings = new frmSettings();
            formSettings.ShowDialog();
            textBox1.AppendText("Poll loop detenido\r\n");
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            timer1.Enabled = false;
        }

        private void btnHalt_Click(object sender, EventArgs e)
        {
            textBox1.AppendText("Poll loop detenido\r\n");
            Running = false;
        }

        private void payoutBtn_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
            {
                // make sure payout is switched on
                NV11.EnablePayout();
                NV11.PayoutNextNote(textBox1);
            }
        }

        private void btnReturn_Click(object sender, EventArgs e)
        {
            NV11.ReturnNote(textBox1);
        }

        private void btnSmartEmpty_Click(object sender, EventArgs e)
        {
            NV11.SmartEmpty(textBox1);
        }

        //ACA ES EL REDIRECCIONAMIENTO DEL GUARDADO DE BILLETES DE 10 USD
        private void noteToRecycleComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (NV11 != null)
            {

                if (noteToRecycleComboBox.Text == "No Recycling")
                {
                    // switch all notes to stacking
                    textBox1.AppendText("Resetting note routing...\r\n");
                    NV11.RouteAllToStack(textBox1);
                }
                else if (noteToRecycleComboBox.Text == "Show Routing")
                {
                    textBox1.AppendText("Current note routing:\r\n");
                    NV11.ShowAllRouting(textBox1);
                    UpdateUI();
                }

                else
                {
                    // switch all notes to stacking first
                    //NV11.RouteAllToStack();
                    // make sure payout is switched on
                    NV11.EnablePayout();
                    // switch selected note to payout
                    string s = noteToRecycleComboBox.Items[noteToRecycleComboBox.SelectedIndex].ToString();
                    string[] sArr = s.Split(' ');
                    try
                    {
                        textBox1.AppendText("Changing note routing...\r\n");
                        NV11.ChangeNoteRoute(Int32.Parse(sArr[0]) * 100, sArr[1].ToCharArray(), false, textBox1);
                        NV11.ShowAllRouting(textBox1);
                        UpdateUI();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.ToString());
                        return;
                    }
                }
            }
        }

        private void emptyNoteFloatToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
            {
                // make sure payout is switched on
                NV11.EnablePayout();
                NV11.EmptyPayoutDevice(textBox1);
            }
        }

        private void cashboxBtn_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
                NV11.PayoutNextNote(textBox1);
        }

        private void resetValidatorBtn_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
            {
                NV11.Reset(textBox1);
                NV11.SSPComms.CloseComPort(); // close com port to force reconnect
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            Running = false;
            //Properties.Settings.Default.CommWindow = logTickBox.Checked;
            Properties.Settings.Default.ComPort = Global.ComPort;
            Properties.Settings.Default.Save();
        }

        private void ResetTotalsText_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
            {
                NV11.NotesAccepted = 0;
                NV11.NotesDispensed = 0;
            }
        }

        private void testToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
            {
                // make sure payout is switched on
                NV11.EnablePayout();
                NV11.PayoutNextNote(textBox1);
            }
        }

        private void stackNextNoteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
            {
                // make sure payout is switched on
                NV11.EnablePayout();
                NV11.StackNextNote(textBox1);
            }
        }

        private void emptyStoredNotesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (NV11 != null)
            {
                // make sure payout is switched on
                NV11.EnablePayout();
                NV11.EmptyPayoutDevice();
            }
        }

        private void optionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Running = false;
            frmSettings f = new frmSettings();
            f.Show();
        }

        private void reconnectionTimer_Tick(object sender, EventArgs e)
        {
            if (sender is System.Windows.Forms.Timer)
            {
                System.Windows.Forms.Timer  t = sender as System.Windows.Forms.Timer;
                t.Enabled = false;
            }
        }

        private void modoInsertarBilletesToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            NV11.CommandStructure.ComPort = Global.ComPort;
            NV11.CommandStructure.SSPAddress = Global.SSPAddress;
            NV11.CommandStructure.Timeout = 1500;

            if (ConnectToValidator(reconnectionAttempts))
            {
                textBox1.AppendText("\r\nPoll Loop\r\n*********************************\r\n");
                modoInsertarBilletes = true;
            }

            while (modoInsertarBilletes)
            {
                NV11.EnableValidator(textBox1);

                // if the poll fails, try to reconnect
                if (NV11.DoPoll(0, 10000, 0, textBox1).Key == false)
                {
                    textBox1.AppendText("Poll fallido, intentando reconectar...\r\n");
                    while (true)
                    {
                        // attempt reconnect, pass over number of reconnection attempts
                        if (ConnectToValidator(reconnectionAttempts) == true)
                            break; // if connection successful, break out and carry on
                                   // if not successful, stop the execution of the poll loop
                        btnRun.Enabled = true;
                        btnHalt.Enabled = false;
                        NV11.SSPComms.CloseComPort(); // close com port before return
                        return;
                    }
                    textBox1.AppendText("Billetero reconectado\r\n");
                }

                timer1.Enabled = true;
                // update form
                UpdateUI();

                // setup dynamic elements of win form once
                if (!FormSetup)
                {
                    SetupFormLayout();
                    FormSetup = true;
                }

                while (timer1.Enabled)
                {
                    Application.DoEvents();
                    Thread.Sleep(1); // Yield to free up CPU
                }
            }
        }
    }
}
