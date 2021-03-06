﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetTopologySuite.IO.Properties;

namespace NetTopologySuite.IO.Serialization
{
    internal class Geography
    {
        public int SRID { get; set; }
        public byte Version { get; set; } = 1;
        public List<Point> Points { get; } = new List<Point>();
        public List<double> ZValues { get; } = new List<double>();
        public List<double> MValues { get; } = new List<double>();
        public List<Figure> Figures { get; } = new List<Figure>();
        public List<Shape> Shapes { get; } = new List<Shape>();
        public List<Segment> Segments { get; } = new List<Segment>();
        public bool IsValid { get; set; } = true;
        public bool IsLargerThanAHemisphere { get; set; }

        public static Geography ReadFrom(BinaryReader reader)
        {
            try
            {
                var geography = new Geography
                {
                    SRID = reader.ReadInt32()
                };

                if (geography.SRID == -1)
                {
                    return geography;
                }

                geography.Version = reader.ReadByte();
                if (geography.Version != 1 && geography.Version != 2)
                {
                    throw new FormatException(string.Format(Resources.UnexpectedGeographyVersion, geography.Version));
                }

                var properties = (SerializationProperties)reader.ReadByte();

                geography.IsValid = properties.HasFlag(SerializationProperties.IsValid);

                if (geography.Version == 2)
                {
                    geography.IsLargerThanAHemisphere = properties.HasFlag(SerializationProperties.IsLargerThanAHemisphere);
                }

                int numberOfPoints = properties.HasFlag(SerializationProperties.IsSinglePoint)
                    ? 1
                    : properties.HasFlag(SerializationProperties.IsSingleLineSegment)
                        ? 2
                        : reader.ReadInt32();

                for (int i = 0; i < numberOfPoints; i++)
                {
                    geography.Points.Add(Point.ReadFrom(reader));
                }

                if (properties.HasFlag(SerializationProperties.HasZValues))
                {
                    for (int i = 0; i < numberOfPoints; i++)
                    {
                        geography.ZValues.Add(reader.ReadDouble());
                    }
                }

                if (properties.HasFlag(SerializationProperties.HasMValues))
                {
                    for (int i = 0; i < numberOfPoints; i++)
                    {
                        geography.MValues.Add(reader.ReadDouble());
                    }
                }

                bool hasSegments = false;

                if (properties.HasFlag(SerializationProperties.IsSinglePoint)
                    || properties.HasFlag(SerializationProperties.IsSingleLineSegment))
                {
                    geography.Figures.Add(
                        new Figure
                        {
                            FigureAttribute = FigureAttribute.PointOrLine,
                            PointOffset = 0
                        });
                }
                else
                {
                    int numberOfFigures = reader.ReadInt32();

                    for (int i = 0; i < numberOfFigures; i++)
                    {
                        var figure = Figure.ReadFrom(reader);

                        if (geography.Version == 1)
                        {
                            // NB: The legacy value is ignored. Exterior rings are always first
                            figure.FigureAttribute = FigureAttribute.PointOrLine;
                        }
                        else if (figure.FigureAttribute == FigureAttribute.Curve)
                        {
                            hasSegments = true;
                        }

                        geography.Figures.Add(figure);
                    }
                }

                if (properties.HasFlag(SerializationProperties.IsSinglePoint)
                    || properties.HasFlag(SerializationProperties.IsSingleLineSegment))
                {
                    geography.Shapes.Add(
                        new Shape
                        {
                            ParentOffset = -1,
                            FigureOffset = 0,
                            Type = properties.HasFlag(SerializationProperties.IsSinglePoint)
                                ? OpenGisType.Point
                                : OpenGisType.LineString
                        });
                }
                else
                {
                    int numberOfShapes = reader.ReadInt32();

                    for (int i = 0; i < numberOfShapes; i++)
                    {
                        geography.Shapes.Add(Shape.ReadFrom(reader));
                    }
                }

                if (hasSegments)
                {
                    int numberOfSegments = reader.ReadInt32();

                    for (int i = 0; i < numberOfSegments; i++)
                    {
                        geography.Segments.Add(Segment.ReadFrom(reader));
                    }
                }

                return geography;
            }
            catch (EndOfStreamException ex)
            {
                throw new FormatException(Resources.UnexpectedEndOfStream, ex);
            }
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SRID);

            if (SRID == -1)
            {
                return;
            }

            writer.Write(Version);

            var properties = SerializationProperties.None;
            if (ZValues.Count > 0)
            {
                properties |= SerializationProperties.HasZValues;
            }
            if (MValues.Count > 0)
            {
                properties |= SerializationProperties.HasMValues;
            }
            if (IsValid)
            {
                properties |= SerializationProperties.IsValid;
            }
            if (Shapes.First().Type == OpenGisType.Point && Points.Count > 0)
            {
                properties |= SerializationProperties.IsSinglePoint;
            }
            if (Shapes.First().Type == OpenGisType.LineString && Points.Count == 2)
            {
                properties |= SerializationProperties.IsSingleLineSegment;
            }
            if (Version == 2 && IsLargerThanAHemisphere)
            {
                properties |= SerializationProperties.IsLargerThanAHemisphere;
            }
            writer.Write((byte)properties);

            if (!properties.HasFlag(SerializationProperties.IsSinglePoint)
                && !properties.HasFlag(SerializationProperties.IsSingleLineSegment))
            {
                writer.Write(Points.Count);
            }

            foreach (var point in Points)
            {
                point.WriteTo(writer);
            }

            foreach (double z in ZValues)
            {
                writer.Write(z);
            }

            foreach (double m in MValues)
            {
                writer.Write(m);
            }

            if (properties.HasFlag(SerializationProperties.IsSinglePoint)
                || properties.HasFlag(SerializationProperties.IsSingleLineSegment))
            {
                return;
            }

            writer.Write(Figures.Count);

            if (Version == 1)
            {
                // For version 1, we need to keep track of each figure's shape to determine whether a polygon's ring is
                // internal or external
                for (int shapeIndex = 0; shapeIndex < Shapes.Count; shapeIndex++)
                {
                    var shape = Shapes[shapeIndex];
                    if (shape.FigureOffset == -1 || shape.IsCollection())
                    {
                        continue;
                    }

                    int nextShapeIndex = shapeIndex + 1;
                    while (nextShapeIndex < Shapes.Count && Shapes[nextShapeIndex].FigureOffset == -1)
                    {
                        nextShapeIndex++;
                    }

                    int lastFigureIndex = nextShapeIndex >= Shapes.Count
                        ? Figures.Count - 1
                        : Shapes[nextShapeIndex].FigureOffset - 1;

                    if (shape.Type == OpenGisType.Polygon)
                    {
                        // NB: Although never mentioned in MS-SSCLRT (v20170816), exterior rings must be first
                        writer.Write((byte)LegacyFigureAttribute.ExteriorRing);
                        writer.Write(Figures[shape.FigureOffset].PointOffset);

                        for (int figureIndex = shape.FigureOffset + 1; figureIndex <= lastFigureIndex; figureIndex++)
                        {
                            writer.Write((byte)LegacyFigureAttribute.InteriorRing);
                            writer.Write(Figures[figureIndex].PointOffset);
                        }

                        continue;
                    }

                    for (int figureIndex = shape.FigureOffset; figureIndex <= lastFigureIndex; figureIndex++)
                    {
                        writer.Write((byte)LegacyFigureAttribute.Stroke);
                        writer.Write(Figures[figureIndex].PointOffset);
                    }
                }
            }
            else
            {
                foreach (var figure in Figures)
                {
                    figure.WriteTo(writer);
                }
            }

            writer.Write(Shapes.Count);

            foreach (var shape in Shapes)
            {
                shape.WriteTo(writer);
            }

            if (Segments.Count > 0)
            {
                writer.Write(Segments.Count);

                foreach (var segment in Segments)
                {
                    segment.WriteTo(writer);
                }
            }
        }
    }
}
