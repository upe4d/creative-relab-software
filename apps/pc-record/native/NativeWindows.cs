using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Runtime.InteropServices;
namespace PCRecord {
 // Выбор области остаётся в обычном окне Windows и ограничен одним выбранным монитором.
 internal sealed class RegionPicker:Form {
  Point start;Rectangle selected;bool dragging;
  public Rectangle Selected{get{return selected;}}
  public RegionPicker(Screen screen){FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=screen.Bounds;TopMost=true;DoubleBuffered=true;BackColor=Color.FromArgb(18,26,38);Opacity=.65;Cursor=Cursors.Cross;KeyPreview=true;ShowInTaskbar=false;}
  protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);start=e.Location;dragging=true;Capture=true;}
  protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(!dragging)return;int x=Math.Max(0,Math.Min(start.X,e.X)),y=Math.Max(0,Math.Min(start.Y,e.Y));int right=Math.Min(ClientSize.Width,Math.Max(start.X,e.X)),bottom=Math.Min(ClientSize.Height,Math.Max(start.Y,e.Y));selected=Rectangle.FromLTRB(x,y,right,bottom);Invalidate();}
  protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;if(selected.Width>=16&&selected.Height>=16){DialogResult=DialogResult.OK;Close();}}
  protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}}
  protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var font=new Font("Segoe UI",16)){e.Graphics.DrawString("Выделите область мышью · Esc — отмена",font,Brushes.White,24,24);}using(var pen=new Pen(Color.Cyan,3)){e.Graphics.DrawRectangle(pen,selected);} }
 }
 // Искусственное содержимое для испытаний: чужие окна и личные данные в него не попадают.
 internal sealed class TestPattern:Form {
  readonly Timer animation=new Timer{Interval=40};int frame;
  public TestPattern(){Text="PC Record — тестовое окно";ClientSize=new Size(640,360);StartPosition=FormStartPosition.CenterScreen;BackColor=Color.FromArgb(18,28,44);DoubleBuffered=true;animation.Tick+=(s,e)=>{frame++;Invalidate();};animation.Start();}
  protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var heading=new Font("Segoe UI",16,FontStyle.Bold))using(var body=new Font("Segoe UI",13)){e.Graphics.DrawString("PC RECORD / TEST",heading,Brushes.White,25,35);e.Graphics.DrawString("Только искусственное содержимое · кадр "+frame,body,Brushes.LightSkyBlue,27,96);e.Graphics.FillRectangle(Brushes.DeepSkyBlue,25+(frame*5)%430,155,140,80);e.Graphics.FillRectangle(Brushes.OrangeRed,25,275,140,45);e.Graphics.FillRectangle(Brushes.LimeGreen,185,275,140,45);e.Graphics.FillRectangle(Brushes.White,345,275,140,45);}}
  protected override void Dispose(bool disposing){if(disposing)animation.Dispose();base.Dispose(disposing);}
 }
 internal static class NativeTheme {
  [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
  public static void Apply(Form form,bool dark){int flag=dark?1:0;try{DwmSetWindowAttribute(form.Handle,20,ref flag,4);}catch(DllNotFoundException){} }
 }
}