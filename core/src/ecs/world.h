/**
 * @file world.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief
 * @version 1.0
 * @date 2026-09-11
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once

// TODO: we want a The Machinery style approach here.
// ECS, like other major systems will be gems (runtime loaded plugins).
// So we need gems to have 'interface implementations' things that tell other things how they will be used.
// We will define interfaces here in the core, and then components can implement them. For example:
// A component that implements the 'Renderable' interface
// doesn't mean "call my render function to draw right now."
// It means: "I am giving the Render System a unified callback so it can pull my vertices, materials,
// and matrices into a massive linear buffer."