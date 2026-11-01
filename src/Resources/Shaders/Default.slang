// The default shader, what a project gets when it asks for one: the PBR surface.

module "Default";

__exported import Surfaces.Pbr;

public typealias Default = Pbr;
