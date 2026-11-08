using System;using System.Drawing;using System.Drawing.Drawing2D;using System.Windows.Forms;
namespace CreativeRelab.Updates {
    // Собственная отрисовка сохраняет читаемость отключённых кнопок в тёмной теме.
    internal sealed class RoundedButton:Button {
        private bool hovered;
        internal RoundedButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;}
        protected override void OnMouseEnter(EventArgs e){hovered=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hovered=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
        protected override void OnPaint(PaintEventArgs e){
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;var rectangle=new Rectangle(1,1,Width-3,Height-3);const int radius=12;
            using(var path=new GraphicsPath()){
                path.AddArc(rectangle.Left,rectangle.Top,radius,radius,180,90);path.AddArc(rectangle.Right-radius,rectangle.Top,radius,radius,270,90);path.AddArc(rectangle.Right-radius,rectangle.Bottom-radius,radius,radius,0,90);path.AddArc(rectangle.Left,rectangle.Bottom-radius,radius,radius,90,90);path.CloseFigure();
                using(var brush=new SolidBrush(Enabled?(hovered?Color.FromArgb(42,58,82):Color.FromArgb(29,40,57)):Color.FromArgb(24,30,40)))e.Graphics.FillPath(brush,path);
                using(var pen=new Pen(Focused?Color.FromArgb(146,181,255):Color.FromArgb(65,77,95)))e.Graphics.DrawPath(pen,path);
            }
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled?Color.FromArgb(237,240,247):Color.FromArgb(145,157,177),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        }
    }
}
