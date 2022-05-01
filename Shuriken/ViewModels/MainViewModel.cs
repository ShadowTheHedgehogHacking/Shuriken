﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Collections.ObjectModel;
using XNCPLib.XNCP;
using XNCPLib.XNCP.Animation;
using Shuriken.Models;
using Shuriken.Commands;
using System.Windows;
using Shuriken.Misc;
using System.Reflection;
using Shuriken.Models.Animation;

namespace Shuriken.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version.ToString();

        public List<string> MissingTextures { get; set; }
        public ObservableCollection<ViewModelBase> Editors { get; set; }

        // File Info
        public FAPCFile WorkFile { get; set; }
        public string WorkFilePath { get; set; }
        public bool IsLoaded { get; set; }
        public MainViewModel()
        {
            MissingTextures = new List<string>();

            Editors = new ObservableCollection<ViewModelBase>
            {
                new ScenesViewModel(),
                new SpritesViewModel(),
                new FontsViewModel(),
                new AboutViewModel()
            };

            IsLoaded = false;
#if DEBUG
            //LoadTestXNCP();
#endif
        }

        public void LoadTestXNCP()
        {
            Load("Test/ui_gameplay.xncp");
        }

        /// <summary>
        /// Loads a Ninja Chao Project file for editing
        /// </summary>
        /// <param name="filename">The path of the file to load</param>
        public void Load(string filename)
        {
            WorkFile = new FAPCFile();
            WorkFile.Load(filename);

            string root = Path.GetDirectoryName(Path.GetFullPath(filename));

            List<Scene> xScenes = WorkFile.Resources[0].Content.CsdmProject.Root.Scenes;
            List<SceneID> xSceneIDs = WorkFile.Resources[0].Content.CsdmProject.Root.SceneIDTable;
            List<XTexture> xTextures = WorkFile.Resources[1].Content.TextureList.Textures;
            FontList xFontList = WorkFile.Resources[0].Content.CsdmProject.Fonts;

            Clear();

            TextureList texList = new TextureList("textures");
            foreach (XTexture texture in xTextures)
            {
                string texPath = Path.Combine(root, texture.Name);
                if (File.Exists(texPath))
                    texList.Textures.Add(new Texture(texPath));
                else
                    MissingTextures.Add(texture.Name);
            }

            if (MissingTextures.Count > 0)
                WarnMissingTextures();

            if (xScenes.Count > 0)
            {
                // Hack: we load sprites from the first scene only since whatever tool sonic team uses
                // seems to work the same way as SWIF:
                // Sprites belong to textures and layers and fonts reference a specific sprite using the texutre index and sprite index.
                int subImageIndex = 0;
                foreach (SubImage subimage in xScenes[0].SubImages)
                {
                    int textureIndex = (int)subimage.TextureIndex;
                    if (textureIndex >= 0 && textureIndex < texList.Textures.Count)
                    {
                        int id = Project.CreateSprite(texList.Textures[textureIndex], subimage.TopLeft.Y, subimage.TopLeft.X,
                            subimage.BottomRight.Y, subimage.BottomRight.X);
                        
                        texList.Textures[textureIndex].Sprites.Add(id);
                    }
                    ++subImageIndex;
                }
            }

            List<FontID> fontIDSorted = xFontList.FontIDTable.OrderBy(o => o.Index).ToList();
            for (int i = 0; i < xFontList.FontIDTable.Count; i++)
            {
                int id = Project.CreateFont(fontIDSorted[i].Name);
                UIFont font = Project.TryGetFont(id);
                foreach (var mapping in xFontList.Fonts[i].CharacterMappings)
                {
                    var sprite = Utilities.FindSpriteIDFromNCPScene((int)mapping.SubImageIndex, xScenes[0].SubImages, texList.Textures);
                    font.Mappings.Add(new Models.CharacterMapping(mapping.SourceCharacter, sprite));
                }
            }

            List<SceneID> xSceneIDSorted = xSceneIDs.OrderBy(o => o.Index).ToList();
            for (int i = 0; i < xScenes.Count; i++)
            {
                Project.Scenes.Add(new UIScene(xScenes[i], xSceneIDSorted[i].Name, texList));
            }

            Project.TextureLists.Add(texList);

            WorkFilePath = filename;
            IsLoaded = !MissingTextures.Any();
        }

        // Very barebones save method which doesn't add anything into the original NCP file, and only changes what's already there
        // It also *may* not save everything, but it's progress...
        public void Save(string path)
        {
            if (path == null) path = WorkFilePath;
            else WorkFilePath = path;

            // TODO: We should create a FACPFile from scratch instead of overwritting the working one

            string root = Path.GetDirectoryName(Path.GetFullPath(WorkFilePath));

            List<Scene> xScenes = WorkFile.Resources[0].Content.CsdmProject.Root.Scenes;
            List<SceneID> xIDs = WorkFile.Resources[0].Content.CsdmProject.Root.SceneIDTable;
            List<XTexture> xTextures = WorkFile.Resources[1].Content.TextureList.Textures;
            FontList xFontList = WorkFile.Resources[0].Content.CsdmProject.Fonts;

            List<SubImage> subImageList = BuildSubImageList();
            SaveTextures(xTextures);
            SaveFonts(xFontList, subImageList);

            List<System.Numerics.Vector2> Data1 = new List<System.Numerics.Vector2>();
            TextureList texList = Project.TextureLists[0];
            foreach (Texture tex in texList.Textures)
            {
                Data1.Add(new System.Numerics.Vector2((float)tex.Width / 1280F, (float)tex.Height / 720F));
            }
            SaveScenes(new CSDNode(), subImageList, Data1);

            foreach (SceneID sceneID in xIDs)
            {
                Scene scene = xScenes[(int)sceneID.Index];
                UIScene uiScene = Project.Scenes[(int)sceneID.Index];

                sceneID.Name = uiScene.Name.Substring(0, sceneID.Name.Length); // TODO: This will break with names larger than the original one

                scene.Field00 = uiScene.Field00;
                scene.ZIndex = uiScene.ZIndex;
                scene.Field0C = uiScene.Field0C;
                scene.Field10 = uiScene.Field10;
                scene.AspectRatio = uiScene.AspectRatio;
                scene.AnimationFramerate = uiScene.AnimationFramerate;

                int textureSizeIndex = 0;
                for (int i = 0; i < scene.Data1.Count; ++i)
                {
                    scene.Data1[i] = uiScene.TextureSizes[textureSizeIndex++];
                }

                SaveCasts(uiScene, scene);
            }

            WorkFile.Save(path);
        }

        private List<SubImage> BuildSubImageList()
        {
            List<SubImage> newSubImages = new List<SubImage>();
            TextureList texList = Project.TextureLists[0];
            foreach (var entry in Project.Sprites)
            {
                Sprite sprite = entry.Value;
                int textureIndex = texList.Textures.IndexOf(sprite.Texture);

                SubImage subimage = new SubImage();
                subimage.TextureIndex = (uint)textureIndex;
                subimage.TopLeft = new Vector2((float)sprite.X / sprite.Texture.Width, (float)sprite.Y / sprite.Texture.Height);
                subimage.BottomRight = new Vector2((float)(sprite.X + sprite.Width) / sprite.Texture.Width, (float)(sprite.Y + sprite.Height) / sprite.Texture.Height);
                newSubImages.Add(subimage);
            }

            return newSubImages;
        }

        private void SaveTextures(List<XTexture> xTextures)
        {
            xTextures.Clear();
            TextureList texList = Project.TextureLists[0];
            foreach (Texture texture in texList.Textures)
            {
                XTexture xTexture = new XTexture();
                xTexture.Name = texture.Name + ".dds";
                xTextures.Add(xTexture);
            }
        }

        private void SaveFonts(FontList xFontList, List<SubImage> subImageList)
        {
            xFontList.Fonts.Clear();
            xFontList.FontIDTable.Clear();

            TextureList texList = Project.TextureLists[0];
            foreach (var entry in Project.Fonts)
            {
                UIFont uiFont = entry.Value;

                // NOTE: need to sort by name after
                FontID fontID = new FontID();
                fontID.Index = (uint)xFontList.FontIDTable.Count;
                fontID.Name = uiFont.Name;
                xFontList.FontIDTable.Add(fontID);

                Font font = new Font();
                foreach (var mapping in uiFont.Mappings)
                {
                    // This seems to work fine, but causes different values to be saved in ui_gameplay.xncp. Duplicate subimage entry?
                    XNCPLib.XNCP.CharacterMapping characterMapping = new XNCPLib.XNCP.CharacterMapping();
                    characterMapping.SubImageIndex = Utilities.FindSubImageIndexFromSprite(Project.TryGetSprite(mapping.Sprite), subImageList, texList.Textures);
                    characterMapping.SourceCharacter = mapping.Character;
                    font.CharacterMappings.Add(characterMapping);
                }
                xFontList.Fonts.Add(font);
            }

            // Sort font names
            xFontList.FontIDTable = xFontList.FontIDTable.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
        }

        private void SaveScenes(CSDNode xNode, List<SubImage> subImageList, List<System.Numerics.Vector2> Data1)
        {
            // TODO: sub nodes, sort sub node names

            xNode.Scenes.Clear();
            xNode.SceneIDTable.Clear();

            // Save individual scenes
            for (int s = 0; s < Project.Scenes.Count; s++)
            {
                UIScene uiScene = Project.Scenes[s];
                Scene xScene = new Scene();

                // Save scene parameters
                xScene.Field00 = uiScene.Field00;
                xScene.ZIndex = uiScene.ZIndex;
                xScene.AnimationFramerate = uiScene.AnimationFramerate;
                xScene.Field0C = uiScene.Field0C;
                xScene.Field10 = uiScene.Field10;
                xScene.AspectRatio = uiScene.AspectRatio;
                xScene.Data1 = Data1;
                xScene.SubImages = subImageList;

                for (int g = 0; g < uiScene.Groups.Count; g++)
                {
                    CastGroup xCastGroup = new CastGroup();
                    UICastGroup uiCastGroup = uiScene.Groups[g];

                    xCastGroup.Field08 = uiCastGroup.Field08;

                    // Get 1-dimensional UICast list, this will be in order of casts from top to bottom in UI
                    List<UICast> uiCastList = new List<UICast>();
                    GetAllUICastInGroup(uiCastGroup.Casts, uiCastList);
                    SaveCasts(uiCastList, xCastGroup, subImageList);

                    // Save the hierarchy tree for the current group
                    xCastGroup.CastHierarchyTree = new List<CastHierarchyTreeNode>();
                    xCastGroup.CastHierarchyTree.AddRange
                    (
                        Enumerable.Repeat(new CastHierarchyTreeNode(-1, -1), uiCastList.Count)
                    );
                    SaveHierarchyTree(uiCastGroup.Casts, uiCastList, xCastGroup.CastHierarchyTree);

                    // Add cast name to dictionary, NOTE: this need to be sorted after
                    for (int c = 0; c < uiCastList.Count; c++)
                    {
                        CastDictionary castDictionary = new CastDictionary();
                        castDictionary.Name = uiCastList[c].Name;
                        castDictionary.GroupIndex = (uint)g;
                        castDictionary.CastIndex = (uint)c;
                        xScene.CastDictionaries.Add(castDictionary);
                    }

                    xScene.UICastGroups.Add(xCastGroup);
                }

                // Sort cast names
                xScene.CastDictionaries = xScene.CastDictionaries.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();

                // TODO: AnimationKeyframeDataList, AnimationData2List

                foreach (AnimationGroup animGroup in uiScene.Animations)
                {
                    // Add animation names, NOTE: need to be sorted after
                    AnimationDictionary animationDictionary = new AnimationDictionary();
                    animationDictionary.Index = (uint)xScene.AnimationDictionaries.Count;
                    animationDictionary.Name = animGroup.Name;
                    xScene.AnimationDictionaries.Add(animationDictionary);

                    // AnimationFrameDataList
                    AnimationFrameData animationFrameData = new AnimationFrameData();
                    animationFrameData.Field00 = animGroup.Field00;
                    animationFrameData.FrameCount = animGroup.Duration;
                    xScene.AnimationFrameDataList.Add(animationFrameData);
                }

                // Sort animation names
                xScene.AnimationDictionaries = xScene.AnimationDictionaries.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();

                // Add scene name to dictionary, NOTE: this need to sorted after
                SceneID xSceneID = new SceneID();
                xSceneID.Name = uiScene.Name;
                xSceneID.Index = (uint)s;
                xNode.SceneIDTable.Add(xSceneID);

                xNode.Scenes.Add(xScene);
            }

            // Sort scene names
            xNode.SceneIDTable = xNode.SceneIDTable.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
        }

        private void GetAllUICastInGroup(ObservableCollection<UICast> children, List<UICast> uiCastList)
        {
            foreach (UICast uiCast in children)
            {
                uiCastList.Add(uiCast);
                GetAllUICastInGroup(uiCast.Children, uiCastList);
            }
        }

        private void SaveHierarchyTree(ObservableCollection<UICast> children, List<UICast> uiCastList, List<CastHierarchyTreeNode> tree)
        {
            for (int i = 0; i < children.Count; i++)
            {
                UICast uiCast = children[i];

                int currentIndex = uiCastList.IndexOf(uiCast);
                Debug.Assert(currentIndex != -1);
                CastHierarchyTreeNode castHierarchyTreeNode = new CastHierarchyTreeNode(-1, -1);

                if (uiCast.Children.Count > 0)
                {
                    castHierarchyTreeNode.ChildIndex = uiCastList.IndexOf(uiCast.Children[0]);
                    Debug.Assert(castHierarchyTreeNode.ChildIndex != -1);
                }

                if (i + 1 < children.Count)
                {
                    castHierarchyTreeNode.NextIndex = uiCastList.IndexOf(children[i + 1]);
                    Debug.Assert(castHierarchyTreeNode.NextIndex != -1);
                }

                tree[currentIndex] = castHierarchyTreeNode;
                SaveHierarchyTree(uiCast.Children, uiCastList, tree);
            }
        }

        private void SaveCasts(List<UICast> uiCastList, CastGroup xCastGroup, List<SubImage> subImageList)
        {
            foreach (UICast uiCast in uiCastList)
            {
                Cast xCast = new Cast();

                xCast.Field00 = uiCast.Field00;
                xCast.Field04 = (uint)uiCast.Type;
                xCast.IsEnabled = uiCast.IsEnabled ? 1u : 0u;

                xCast.TopLeft = new Vector2(uiCast.TopLeft);
                xCast.TopRight = new Vector2(uiCast.TopRight);
                xCast.BottomLeft = new Vector2(uiCast.BottomLeft);
                xCast.BottomRight = new Vector2(uiCast.BottomRight);

                xCast.Field2C = uiCast.Field2C;
                xCast.Field34 = uiCast.Field34;
                xCast.Field38 = uiCast.Flags;
                xCast.Field3C = uiCast.Field3C;

                xCast.FontCharacters = uiCast.FontCharacters;
                if (uiCast.Type == DrawType.Font)
                {
                    UIFont uiFont = Project.TryGetFont(uiCast.FontID);
                    if (uiFont != null)
                    {
                        xCast.FontName = uiFont.Name;
                    }
                }
                xCast.FontSpacingAdjustment = uiCast.FontSpacingAdjustment;

                xCast.Width = uiCast.Width;
                xCast.Height = uiCast.Height;
                xCast.Field58 = uiCast.Field58;
                xCast.Field5C = uiCast.Field5C;

                xCast.Offset = new Vector2(uiCast.Offset);
                
                xCast.Field68 = uiCast.Field68;
                xCast.Field6C = uiCast.Field6C;
                xCast.Field70 = uiCast.Field70;

                // Cast Info
                xCast.CastInfoData = new CastInfo();
                xCast.CastInfoData.Field00 = uiCast.InfoField00;
                xCast.CastInfoData.Translation = new Vector2(uiCast.Translation);
                xCast.CastInfoData.Rotation = uiCast.Rotation;
                xCast.CastInfoData.Scale = new Vector2(uiCast.Scale.X, uiCast.Scale.Y);
                xCast.CastInfoData.Field18 = uiCast.InfoField18;
                xCast.CastInfoData.Color = uiCast.Color.ToUint();
                xCast.CastInfoData.GradientTopLeft = uiCast.GradientTopLeft.ToUint();
                xCast.CastInfoData.GradientBottomLeft = uiCast.GradientBottomLeft.ToUint();
                xCast.CastInfoData.GradientTopRight = uiCast.GradientTopRight.ToUint();
                xCast.CastInfoData.GradientBottomRight = uiCast.GradientBottomRight.ToUint();
                xCast.CastInfoData.Field30 = uiCast.InfoField30;
                xCast.CastInfoData.Field34 = uiCast.InfoField34;
                xCast.CastInfoData.Field38 = uiCast.InfoField38;

                // Cast Material Info
                xCast.CastMaterialData = new CastMaterialInfo();
                Debug.Assert(uiCast.Sprites.Count == 32);
                for (int index = 0; index < 32; index++)
                {
                    if (uiCast.Sprites[index] == -1)
                    {
                        xCast.CastMaterialData.SubImageIndices[index] = -1;
                        continue;
                    }

                    Sprite uiSprite = Project.TryGetSprite(uiCast.Sprites[index]);
                    xCast.CastMaterialData.SubImageIndices[index] = (int)Utilities.FindSubImageIndexFromSprite(uiSprite, subImageList, Project.TextureLists[0].Textures);
                }

                xCastGroup.Casts.Add(xCast);
            }
        }

        private void SaveCasts(UIScene uiScene, Scene scene)
        {
            // TODO: Deprecate this
            for (int g = 0; g < scene.UICastGroups.Count; ++g)
            {
                scene.UICastGroups[g].Field08 = uiScene.Groups[g].Field08;
            }

            // Pre-process animations
            List<AnimationDictionary> AnimIDSorted = scene.AnimationDictionaries.OrderBy(o => o.Index).ToList();
            for (int a = 0; a < scene.AnimationFrameDataList.Count; a++)
            {
                scene.AnimationFrameDataList[a].Field00 = uiScene.Animations[a].Field00;
                scene.AnimationFrameDataList[a].FrameCount = uiScene.Animations[a].Duration;
            }

            // process group layers
            for (int g = 0; g < uiScene.Groups.Count; ++g)
            {
                for (int c = 0; c < scene.UICastGroups[g].Casts.Count; ++c)
                {
                    Cast cast = scene.UICastGroups[g].Casts[c];
                    UICast uiCast = uiScene.Groups[g].CastsOrderedByIndex[c];

                    cast.Field00 = uiCast.Field00;
                    cast.Field04 = (uint)uiCast.Type;
                    cast.IsEnabled = uiCast.IsEnabled ? (uint)1 : 0;

                    /*
                    float right = Math.Abs(cast.TopRight.X) - Math.Abs(cast.TopLeft.X);
                    float top = Math.Abs(cast.TopRight.Y) - Math.Abs(cast.BottomRight.Y);
                    Anchor = new Vector2(right, top);
                    */

                    cast.TopLeft = new Vector2(uiCast.TopLeft);
                    cast.TopRight = new Vector2(uiCast.TopRight);
                    cast.BottomLeft = new Vector2(uiCast.BottomLeft);
                    cast.BottomRight = new Vector2(uiCast.BottomRight);

                    cast.Field2C = uiCast.Field2C;
                    cast.Field34 = uiCast.Field34;
                    cast.Field38 = uiCast.Flags;
                    cast.Field3C = uiCast.Field3C;

                    cast.FontCharacters = uiCast.FontCharacters;

                    cast.FontSpacingAdjustment = uiCast.FontSpacingAdjustment;
                    cast.Width = uiCast.Width;
                    cast.Height = uiCast.Height;
                    cast.Field58 = uiCast.Field58;
                    cast.Field5C = uiCast.Field5C;

                    cast.Offset = new Vector2(uiCast.Offset);

                    cast.Field68 = uiCast.Field68;
                    cast.Field6C = uiCast.Field6C;
                    cast.FontSpacingAdjustment = uiCast.FontSpacingAdjustment;

                    // Cast Info
                    cast.CastInfoData.Field00 = uiCast.InfoField00;
                    cast.CastInfoData.Translation = new Vector2(uiCast.Translation);
                    cast.CastInfoData.Rotation = uiCast.Rotation;
                    cast.CastInfoData.Scale = new Vector2(uiCast.Scale.X, uiCast.Scale.Y);

                    cast.CastInfoData.Field00 = uiCast.InfoField00;
                    cast.CastInfoData.Color = uiCast.Color.ToUint();
                    cast.CastInfoData.GradientTopLeft = uiCast.GradientTopLeft.ToUint();
                    cast.CastInfoData.GradientBottomLeft = uiCast.GradientBottomLeft.ToUint();
                    cast.CastInfoData.GradientTopRight = uiCast.GradientTopRight.ToUint();
                    cast.CastInfoData.GradientBottomRight = uiCast.GradientBottomRight.ToUint();
                    cast.CastInfoData.Field30 = uiCast.InfoField30;
                    cast.CastInfoData.Field34 = uiCast.InfoField34;
                    cast.CastInfoData.Field38 = uiCast.InfoField38;

                    if (uiCast.Type == DrawType.Sprite)
                    {
                        int[] castSprites = cast.CastMaterialData.SubImageIndices;
                        for (int index = 0; index < uiCast.Sprites.Count; ++index)
                        {
                            if (uiCast.Sprites[index] == -1)
                            {
                                castSprites[index] = -1;
                                continue;
                            }

                            Sprite uiSprite = Project.TryGetSprite(uiCast.Sprites[index]);

                            // TODO: Doesn't support new sprites
                            castSprites[index] = (int)Utilities.FindSubImageIndexFromSprite(uiSprite, scene.SubImages, Project.TextureLists[0].Textures);
                        }
                        
                    }
                    else if (uiCast.Type == DrawType.Font)
                    {
                        foreach (var font in Project.Fonts)
                        {
                            UIFont uiFont = Project.TryGetFont(uiCast.FontID);
                            if (uiFont != null)
                                cast.FontName = uiFont.Name;
                        }
                    }
                    
                }

                for (int a = 0; a < scene.AnimationFrameDataList.Count; a++)
                {
                    int trackIndex = 0;
                    int trackAnimIndex = 0;
                    XNCPLib.XNCP.Animation.AnimationKeyframeData keyframeData = scene.AnimationKeyframeDataList[a];
                    for (int c = 0; c < keyframeData.GroupAnimationDataList[g].CastAnimationDataList.Count; ++c)
                    {
                        XNCPLib.XNCP.Animation.CastAnimationData castAnimData = keyframeData.GroupAnimationDataList[g].CastAnimationDataList[c];

                        int castAnimDataIndex = 0;
                        List<AnimationTrack> tracks = null;
                        for (int i = 0; i < 12; ++i)
                        {
                            // check each animation type if it exists in Flags

                            // TODO: Save new anim flags
                            if ((castAnimData.Flags & (1 << i)) != 0)
                            {

                                if (tracks == null)
                                {
                                    tracks = uiScene.Animations[a].LayerAnimations[trackIndex++].Tracks.ToList();
                                    trackAnimIndex = 0;
                                }
                                AnimationTrack anim = tracks[trackAnimIndex++];

                                castAnimData.SubDataList[castAnimDataIndex].Field00 = anim.Field00;

                                int keyframeIndex = 0;
                                foreach (var key in castAnimData.SubDataList[castAnimDataIndex].Keyframes)
                                {
                                    var uiKey = anim.Keyframes[keyframeIndex++];

                                    key.Frame = uiKey.HasNoFrame ? 0xFFFFFFFF : (uint)uiKey.Frame;
                                    key.Value = uiKey.KValue;
                                    key.Field08 = (uint)uiKey.Field08;
                                    key.Offset1 = uiKey.Offset1;
                                    key.Offset2 = uiKey.Offset2;
                                    key.Field14 = (uint)uiKey.Field14;
                                }

                                ++castAnimDataIndex;
                            }
                        }
                    }
                }

                // TODO: Save Cast Hierarchy tree
            }
        }

        public void Clear()
        {
            Project.Clear();
            MissingTextures.Clear();
        }

        private void WarnMissingTextures()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("The loaded UI file uses textures that were not found. Saving has been disabled. In order to save, please copy the files listed below into the UI file's directory, and re-open it.\n");
            foreach (var texture in MissingTextures)
                builder.AppendLine(texture);

            MessageBox.Show(builder.ToString(), "Missing Textures", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
