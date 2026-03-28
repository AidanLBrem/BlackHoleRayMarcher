
This is a GPU accelerated Raymarcher that implements BVH (Bounded Volume Hierarchy) and stochastic path tracing to visualize black hole relativistic light bending in everyday scenes in the Unity editor with GGX material properties. When no black hole is present, the tracer defaults to a standard linear path tracer. Additionally, some support is given for atmosphereic scattering and some mild NEE, but since such occlusion computations are extremely expensive to perform when scattering, it IS NOT performant. Looking into ways to make curved space shadow maps, because currently all lighting is real time!

A very special thanks to Sebastian Lague - the bones of the ray tracer come from his phenomenal raytracing series on YouTube! 

Gallery:
<img width="1584" height="888" alt="Screenshot 2026-03-25 202041" src="https://github.com/user-attachments/assets/c0a90225-22d3-4ffc-8404-b89d3318c8e7" />
<img width="1589" height="883" alt="Screenshot 2026-03-25 202407" src="https://github.com/user-attachments/assets/b31929a0-0d03-4f53-8576-541b550424fb" />
<img width="1579" height="880" alt="Screenshot 2026-03-25 223936" src="https://github.com/user-attachments/assets/4fcc962d-d924-4e33-bd87-143b2343389f" />
<img width="1575" height="883" alt="Screenshot 2026-03-27 194634" src="https://github.com/user-attachments/assets/f0be0ec5-dca8-4a66-af90-dc0c2645f3da" />
<img width="1580" height="885" alt="Screenshot 2026-03-27 200804" src="https://github.com/user-attachments/assets/f88dc0a7-d005-408c-8413-a1b48a6e27b3" />
<img width="1577" height="886" alt="Screenshot 2026-03-25 224638" src="https://github.com/user-attachments/assets/5c81aa55-6afe-453c-b88f-49257ef1a102" />
