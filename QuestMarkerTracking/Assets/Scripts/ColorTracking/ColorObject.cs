using OpenCVForUnity.CoreModule;
using UnityEngine;

namespace TryAR.ColorTracking
{
    public class ColorObject
    {
        private string type;
        private Scalar HSVmin, HSVmax;
        private Scalar color;
        private Point position;
        private double radius;

        public ColorObject(string name = "")
        {
            setType(name);
            
            // Standardwerte für Pink
            if (name == "pink")
            {
                HSVmin = new Scalar(150, 100, 100);  // Optimiert für den pinken Ball
                HSVmax = new Scalar(165, 255, 255);  // Engerer Farbbereich für das spezifische Pink
                color = new Scalar(255, 0, 255, 255);
            }
        }

        public void setType(string name) { type = name; }
        public string getType() { return type; }
        
        public void setHSVmin(Scalar min) { HSVmin = min; }
        public Scalar getHSVmin() { return HSVmin; }
        
        public void setHSVmax(Scalar max) { HSVmax = max; }
        public Scalar getHSVmax() { return HSVmax; }
        
        public void setColor(Scalar c) { color = c; }
        public Scalar getColor() { return color; }
        
        public void setPosition(Point p) { position = p; }
        public Point getPosition() { return position; }
        
        public void setRadius(double r) { radius = r; }
        public double getRadius() { return radius; }

        public void setHSVRanges(Vector3 min, Vector3 max)
        {
            HSVmin = new Scalar(min.x, min.y, min.z);
            HSVmax = new Scalar(max.x, max.y, max.z);
        }
    }
} 